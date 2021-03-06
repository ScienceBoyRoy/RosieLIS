﻿Imports RoyHarmon.RosieLIS.ICommand
Imports System.Data.Common
Imports System.IO
Imports System.IO.Ports

Public Class RosieLISService

     Public WithEvents ComPort As SerialPort
     Private intTemp As Integer

     Protected Sub OnStart()
          Try
               ' Set up the serial port so the service can do its work.
               SerialOpen()
          Catch ex As Exception
               ' Write any exceptions to the event log.
               HandleError(ex)
               Throw
          End Try
          Try
               Using cnSQL As DbConnection = GetConnected()
                    ' This is just to know from the start whether the server connection will work.
               End Using
          Catch ex As Exception
               ' If there was a problem with the connection, write it to the log.
               HandleError(ex)
               Throw
          End Try
     End Sub

     Sub SerialOpen()
          ComPort = My.Computer.Ports.OpenSerialPort(My.Settings.portName, My.Settings.baudRate, My.Settings.parity, My.Settings.dataBits, My.Settings.stopBits)
          With ComPort
               .Handshake = My.Settings.handShake
               .NewLine = Chr(3)
          End With
     End Sub

     Shared Function GetConnected() As IDbConnection
          Dim cnSQL As IDbConnection
          Select Case My.Settings.databaseType
               Case "SQL Server"
                    cnSQL = New SqlClient.SqlConnection()
               'Case "Oracle" ' Native Oracle client has been deprecated.
               '     cnSQL = New Data.OracleClient.OracleConnection()
               Case "MySQL"
                    cnSQL = New MySql.Data.MySqlClient.MySqlConnection()
               Case "ODBC"
                    cnSQL = New Odbc.OdbcConnection()
               Case Else
                    cnSQL = New OleDb.OleDbConnection()
          End Select
          cnSQL.ConnectionString = My.Settings.connectionString
          cnSQL.Open()
          Return cnSQL
     End Function
     Protected Sub OnStop()
          ' Definitely want to close the COM port when we're done.
          Try
               If ComPort IsNot Nothing Then
                    If ComPort.IsOpen Then ComPort.Close()
                    ComPort.Dispose()
                    ComPort = Nothing
               End If
          Catch ex As Exception
               HandleError(ex)
               Throw
          End Try
     End Sub

     Shared Sub HandleError(ex As Exception)
          If ex Is Nothing Then Exit Sub
          Dim message As String = ex.Source & " - Error: " & ex.Message & " at RosieLIS Line " & ex.LineNumber()
          message &= vbCrLf & "Exception: " & ex.ToString()
          AppendToLog(message)
          EventLog.WriteEntry(ex.Source, message, EventLogEntryType.Error)
     End Sub

     Private Sub Com1_DataReceived(ByVal sender As Object, ByVal e As SerialDataReceivedEventArgs) Handles ComPort.DataReceived
          ReceiveSerialData()
     End Sub

     Sub SendCommData(ByVal strData As String)
          ' Send strings to a serial port.
          ' Properties should already be set.
          Try
               If Not ComPort.IsOpen Then
                    ComPort.Open()
               End If
               ComPort.Write(strData)
               AppendToLog("O: " & strData)
          Catch ex As Exception
               HandleError(ex)
               Throw
          End Try

     End Sub

     Sub ReceiveSerialData()
          ' Receive strings from a serial port.
          ' Properties should already be set.
          Dim strTrans As String = ""

          If ComPort.BytesToRead = 0 Then Exit Sub

          Dim Incoming As String = ""
          Dim intCom As Integer

          Do
               If ComPort.BytesToRead > 0 Then
                    intCom = ComPort.ReadByte()
                    If intCom > 0 Then
                         Incoming = Chr(intCom)
                         'Else
                         '     Incoming = Chr(3)
                    End If
                    If Incoming = Chr(3) Then
                         Exit Do
                    Else
                         If Not Incoming = Chr(6) Then strTrans &= Incoming
                    End If
               Else
                    Threading.Thread.Sleep(200)
               End If
          Loop

          If strTrans.Substring(0, 1) = Chr(2) Then strTrans = strTrans.Substring(1)

          If Len(strTrans) > 2 Then
               ' Check the checksum.
               Dim strCheck As String = strTrans.Substring(0, Len(strTrans) - 2)
               Debug.Print(CHKSum(strCheck))
               Dim strData As String = strTrans.Substring(0, Len(strTrans) - 2)
               If CHKSum(strCheck) = Right(strTrans, 2) Then
                    ' Acknowledge receipt (ACK to serial port).
                    SendCommData(Chr(6))
                    ' Handle the incoming data.
                    IncomingData(strData)
               Else
                    ' There was an error in the transmission.
                    ' Send NAK to serial port. The remote client will attempt to send again.
                    SendCommData(Chr(21))
                    ' Log it.
                    AppendToLog("Checksum Mismatch: " & strTrans)
               End If
          End If
     End Sub

     Shared Function CHKSum(strString) As String
          ' This function returns the checksum for the data string passed to it.
          ' If I've done it right, the checksum is calculated by binary 8-bit addition of all included characters
          ' with the 8th or parity bit assumed zero. Carries beyond the 8th bit are lost. The 8-bit result is
          ' converted into two printable ASCII Hex characters ranging from 00 to FF, which are then inserted into
          ' the data stream. Hex alpha characters are always uppercase.

          Dim strTemp As String
          Dim strData As String
          Dim ascSum As Integer
          Dim modVal As Integer
          Dim checkSum As String

          strData = Trim(strString)

          While Len(strData)
               strTemp = Left(strData, 1)
               If Trim(strTemp) <> Chr(2) Then
                    ascSum += Asc(strTemp)
               End If
               strData = Mid(strData, 2)
          End While

          modVal = ascSum Mod 256
          checkSum = Hex(modVal)

          Return Right("0" & checkSum, 2)

     End Function

     Shared Function ResultAcceptance(Optional ByVal resultAccepted As Boolean = True) As String
          ' This function returns a string indicating to the instrument that
          ' the result has been accepted (or rejected) by the LIS computer.
          Dim strHex As String
          strHex = Chr(2) & "M" & Chr(28)
          If resultAccepted Then
               strHex &= "A" & Chr(28) & Chr(28)
          Else
               strHex &= "R" & Chr(28) & 1 & Chr(28)
          End If
          strHex &= CHKSum(strHex) & Chr(3)
          Return strHex
     End Function

     Function PollQueryMessage(ByVal strData As String)
          ' Send either a NoRequestMessage or a SampleRequestMessage.
          Dim strType As String = Mid(strData, 1, 1)
          Try
               Using cnSQL As IDbConnection = GetConnected()
                    Using command As DbCommand = cnSQL.CreateCommand()
                         If strType = "P" Then
                              ' Poll message. If there are any pending sample requests that haven't been sent, send one now.
                              command.CommandText = "SELECT * FROM PendingTests WHERE PendingSending='TRUE'"
                         ElseIf strType = "I" Then
                              ' Query message. This likely requires a different database setup to be useful. 
                              ' For now, just send any pending sample requests for the sample in question.
                              Dim varRes = Split(strData, Chr(28), , CompareMethod.Binary)
                              ' Only one row can be sent at a time because the instrument needs to respond with: 
                              ' 1. an acknowledgement, 2. a request acceptance message, and 3. another poll or query message, which puts us back here.
                              ' Microsoft SQL Server uses "SELECT TOP 1 ..." while everything else uses "SELECT ... LIMIT 1"
                              If My.Settings.databaseType = "SQL Server" Then
                                   command.CommandText = "SELECT TOP 1 * FROM PendingTests WHERE PendingSending='TRUE' AND SampleNo = @SampleNo"
                              Else
                                   command.CommandText = "SELECT * FROM PendingTests WHERE PendingSending='TRUE' AND SampleNo = @SampleNo LIMIT 1"
                              End If
                              command.AddWithValue("@SampleNo", varRes(1))
                         End If
                         Using dr As DbDataReader = command.ExecuteReader
                              If dr.Read() Then
                                   ' If there's at least one row, send the first row.
                                   Dim strTests() As String = Nothing
                                   Dim intTests As Integer, strSampleType As String, intDil As Integer = 1
                                   strSampleType = dr.Item("SampleType")
                                   intTests = My.Settings.maxTests
                                   ' Determine how many tests are requested for this sample.
                                   For i As Integer = 1 To intTests
                                        If Nz(dr.Item("Test" & i)) = "" Then intTests -= 1
                                   Next
                                   ' Resize the array accordingly.
                                   ReDim strTests(intTests - 1)
                                   Dim iTest As Integer = 0
                                   ' Populate the array.
                                   For i As Integer = 1 To My.Settings.maxTests
                                        If Nz(dr.Item("Test" & i)) <> "" Then
                                             strTests(iTest) = dr.Item("Test" & i)
                                             iTest += 1
                                        End If
                                   Next
                                   If Nz(dr.Item("DilFactor"), 0) > 1 Then intDil = dr.Item("DilFactor")
                                   Dim strOut As String = SampleRequestMessage(dr.Item("ToDelete"), dr.Item("PatientName"), dr.Item("SampleNo"), strSampleType, dr.Item("intPriority"), strTests, intDil)
                                   SendCommData(strOut)
                                   intTemp = dr.Item("Temp_ID")
                                   Return 0
                              Else
                                   SendCommData(NoRequestMessage)
                                   Return 0
                              End If
                         End Using
                    End Using
               End Using
#Disable Warning CA1031 ' Do not catch general exception types
#Disable Warning CA1031 ' Do not catch general exception types
          Catch ex As Exception
#Enable Warning CA1031 ' Do not catch general exception types
               HandleError(ex)
               Return ex.LineNumber()
#Enable Warning CA1031 ' Do not catch general exception types
          End Try
     End Function

     Sub IncomingData(strData As String)
          ' Call a procedure to process the incoming data according to the message type 
          ' based on the first letter of the message.
          Try
               If strData Is Nothing Or Len(strData) = 0 Then
                    Throw New ArgumentNullException(NameOf(strData))
                    Exit Sub
               End If
               Select Case strData.Substring(0, 1)
                    Case "P", "I"
                         ' strData is a Poll message or Query Message.
                         ' Send either a NoRequestMessage or a SampleRequestMessage.
                         PollQueryMessage(strData)

                    Case "M"
                         ' strData is a Request Acceptance message.
                         RequestAcceptanceMessage(strData)

                    Case "R"
                         ' strData is a Result message.
                         ResultMessage(strData)

                    Case "C"
                         '' strData is a Calibration Result message.
                         '' *** 
                         '' *** NOTE: The data transmitted in a Calibration Result message is flawed!
                         '' *** This is inherent in the instrument software's design, mostly the practice of transmitting calibration data 
                         '' *** as soon as the measurements have been completed, rather than waiting until the new coefficients have been generated.
                         '' *** 
                         '' *** The following specific flaws have been observed as of 10/2019 (Siemens Dimension software versions through 10.3):
                         '' ***  -- The Cal_DateTime value actually represents the timestamp from the beginning of the reagent lot's previous calibration.
                         '' ***  -- Due to the above timestamp discrepancy, the first calibration of each lot will have a Cal_DateTime value of "000019311269" (12/31/xx69 7:00 PM).
                         '' ***  -- Since the new coefficients are not yet calculated when the data is transmitted, coefficient values given are from the previous calibration.
                         '' ***  -- All result ("Res##") values are calculated using the current calibration's measurements with the prior calibration's coefficients.
                         '' ***  -- The Cal_Slope value is always given as exactly "1".
                         '' ***  -- The Cal_Intercept value is always given as exactly "0". Since this holds true even when the actual intercept is >1,
                         '' ***     it seems unlikely that this is an integer rounding issue.
                         '' *** 
                         CalibrationResultMessage(strData.Substring(1))

               End Select
               ' Log it.
               AppendToLog("I:  " & strData)

#Disable Warning CA1031 ' Do not catch general exception types
#Disable Warning CA1031 ' Do not catch general exception types
          Catch ex As Exception
#Enable Warning CA1031 ' Do not catch general exception types
               HandleError(ex)
               ComPort.Write(Chr(21))
#Enable Warning CA1031 ' Do not catch general exception types
          End Try

     End Sub

     Function RequestAcceptanceMessage(ByVal strData As String)
          Try
               ' strData contains sample position information, but it is limited.
               ' Sample position always seems to be "42" for accepted tests. 
               ' Rejected or deleted tests will have "0" as the sample position.
               Dim varRes = Split(strData, Chr(28), , vbBinaryCompare)
               Using cnSQL As IDbConnection = GetConnected()
                    Using update As DbCommand = cnSQL.CreateCommand()
                         With update
                              Select Case varRes(1)
                                   Case "R"
                                        ' Request rejected. Reason for rejection is stored in the table. Remember to work that into the front-end.
                                        .CommandText = "UPDATE PendingTests SET PendingTests.Position = @Position, PendingTests.PendingSending = 'FALSE', PendingTests.RejectCode = @RejectCode WHERE PendingTests.Temp_ID = @intTemp"
                                        .AddWithValue("@Position", varRes(5))
                                        .AddWithValue("@RejectCode", varRes(2))
                                        .AddWithValue("@intTemp", intTemp)
                                   Case "A"
                                        ' Request accepted. Set the "position" and clear the PendingSending flag.
                                        .CommandText = "UPDATE PendingTests SET PendingTests.Position = @Position, PendingTests.PendingSending = 'FALSE', PendingTests.ToDelete = 'FALSE', PendingTests.RejectCode = 0 WHERE PendingTests.Temp_ID = @intTemp"
                                        .AddWithValue("@Position", varRes(5))
                                        .AddWithValue("@intTemp", intTemp)
                              End Select
                              .ExecuteNonQuery()
                         End With
                    End Using
               End Using
               AppendToLog("In: " & strData)
               Return 0
#Disable Warning CA1031 ' Do not catch general exception types
#Disable Warning CA1031 ' Do not catch general exception types
          Catch ex As Exception
#Enable Warning CA1031 ' Do not catch general exception types
               HandleError(ex)
               SendCommData(ResultAcceptance(False))
               Return ex.LineNumber()
#Enable Warning CA1031 ' Do not catch general exception types
          End Try
     End Function

     Function CalibrationResultMessage(ByVal strData As String)
          ' strData is a Calibration Result message.
          ' *** 
          ' *** NOTE: The data transmitted in a Calibration Result message is flawed!
          ' *** This is inherent in the instrument software's design, mostly the practice of transmitting calibration data 
          ' *** as soon as the measurements have been completed, rather than waiting until the new coefficients have been generated.
          ' *** 
          ' *** The following specific flaws have been observed as of 10/2019 (Siemens Dimension software versions through 10.3):
          ' ***  -- The Cal_DateTime value actually represents the timestamp from the beginning of the reagent lot's previous calibration.
          ' ***  -- Due to the above timestamp discrepancy, the first calibration of each lot will have a Cal_DateTime value of "000019311269" (12/31/xx69 7:00 PM).
          ' ***  -- Since the new coefficients are not yet calculated when the data is transmitted, coefficient values given are from the previous calibration.
          ' ***  -- All result ("Res##") values are calculated using the current calibration's measurements with the prior calibration's coefficients.
          ' ***  -- The Cal_Slope value is always given as exactly "1".
          ' ***  -- The Cal_Intercept value is always given as exactly "0". Since this holds true even when the actual intercept is >1,
          ' ***     it seems unlikely that this is an integer rounding issue.
          ' *** 
          ' Processing Calibration Result messages can get a little more complicated than most. 
          ' Due to the variations between test methods, not all fields will have a value.
          Dim intVals As Integer, intCoefs As Integer, intField As Integer
          Dim intValCount As Integer = 0
          Dim varRes = Split(strData, Chr(28), , vbBinaryCompare)
          Try
               ' Build the command with parameters.
               Using cnSQL As IDbConnection = GetConnected()
                    Using insert As DbCommand = cnSQL.CreateCommand()
                         With insert
                              .CommandText = "INSERT INTO CalibrationResults (Cal_Test, Cal_Units, Reagent_Lot, Cal_Product, Cal_Prod_Lot, Cal_Op, Cal_DateTime, Cal_Slope, Cal_Intercept, Coefficients_Num, Coefficient_0, Coefficient_1, Coefficient_2, Coefficient_3, Coefficient_4, Bottle_Vals, Val01, Res01, Val02, Res02, Val03, Res03, Val04, Res04, Val05, Res05, Val06, Res06, Val07, Res07, Val08, Res08, Val09, Res09, Val10, Res10, Val11, Res11, Val12, Res12, Val13, Res13, Val14, Res14, Val15, Res15, Val16, Res16, Val17, Res17, Val18, Res18, Val19, Res19, Val20, Res20) VALUES (@Cal_Test, @Cal_Units, @Reagent_Lot, @Cal_Product, @Cal_Prod_Lot, @Cal_Op, @Cal_DateTime, @Cal_Slope, @Cal_Intercept, @Coefficients_Num, @Coefficient_0, @Coefficient_1, @Coefficient_2, @Coefficient_3, @Coefficient_4, @Bottle_Vals, @Val01, @Res01, @Val02, @Res02, @Val03, @Res03, @Val04, @Res04, @Val05, @Res05, @Val06, @Res06, @Val07, @Res07, @Val08, @Res08, @Val09, @Res09, @Val10, @Res10, @Val11, @Res11, @Val12, @Res12, @Val13, @Res13, @Val14, @Res14, @Val15, @Res15, @Val16, @Res16, @Val17, @Res17, @Val18, @Res18, @Val19, @Res19, @Val20, @Res20)"
                              .AddWithValue("@Cal_Test", varRes(1), DbType.String)
                              .AddWithValue("@Cal_Units", varRes(2), DbType.String)
                              .AddWithValue("@Reagent_Lot", varRes(3), DbType.String)
                              .AddWithValue("@Cal_Product", varRes(4), DbType.String)
                              .AddWithValue("@Cal_Prod_Lot", varRes(5), DbType.String)
                              .AddWithValue("@Cal_Op", varRes(6), DbType.String)
                              .AddWithValue("@Cal_DateTime", varRes(7), DbType.String)
                              .AddWithValue("@Cal_Slope", varRes(8), DbType.Double) ' As of Dimension version 10.3, Cal_Slope is always 1.
                              .AddWithValue("@Cal_Intercept", varRes(9), DbType.Double) ' As of Dimension version 10.3, Cal_Intercept is always 0.
                              .AddWithValue("@Coefficients_Num", varRes(10), DbType.Int32)
                              intCoefs = varRes(10) ' Number of coefficients.
                              ' Not all tests have all 5 coefficients.
                              ' Fill in the values for each coefficient.
                              ' Note that intCoefs may be higher than the actual number of coefficients,
                              ' so be sure to fill in zeroes for any empty strings.
                              For i As Integer = intCoefs To 1 Step -1
                                   If varRes(10 + i).Length = 0 Then varRes(10 + i) = "0"
                                   .AddWithValue("@Coefficient_" & i - 1, varRes(10 + i), DbType.Double)
                              Next
                              ' Fill in zeroes for the ones that don't apply.
                              ' If there are more than 4 coefficients, skip this entirely.
                              For i As Integer = intCoefs To 4 Step 1
                                   .AddWithValue("@Coefficient_" & i, "0", DbType.Double)
                              Next
                              .AddWithValue("@Bottle_Vals", varRes(11 + intCoefs), DbType.Int32)
                              intVals = varRes(11 + intCoefs) ' Number of levels tested. Each option (3-5) has a different number of test results.
                              Dim strVals As String, dblLevel As Double, intResults As Integer, intResCount As Integer
                              Dim intCount As Integer = 12 + intCoefs ' intCount keeps track of where we are in the string array. 
                              ' The first 12 elements will always be used for the same fields, but the varying number of coefficients requires us to keep track of where we are from this point onward.
                              intField = 16 ' Keeps our place in the record. Add 16 to account for the parameters we've already filled.
                              ' This next part gets pretty crazy. 
                              ' Turns out the least complicated way I've found is to add all the remaining parameters at once,
                              ' and then we can use an integer variable to cycle through them.
                              For i = 1 To 20
                                   strVals = Right("0" & i, 2)
                                   .AddParam("@Val" & strVals, DbType.Double)
                                   .AddParam("@Res" & strVals, DbType.Double)
                              Next
                              ' Each of the different levels will have its own "bottle value" and number of results.
                              ' For each result at each level, store the "bottle value" in one column (i.e. "Val01"), and the result in another column (i.e. "Res01").
                              ' Some data will be repeated (the "bottle value" is stored twice if there are two results at that level), 
                              ' but since different assays have varying calibration schemes, this seemed most efficient.
                              Do
                                   If IsNumeric(varRes(intCount)) Then dblLevel = varRes(intCount)
                                   intResults = Nz(varRes(intCount + 1), 0)
                                   intResCount = 0
                                   Do Until intResCount = intResults
                                        intResCount += 1
                                        If Not intCount + intResCount + 1 < UBound(varRes, 1) Then Exit Do
                                        If IsNumeric(varRes(intCount)) Then
                                             .Parameters.Item(intField).Value = dblLevel
                                        Else
                                             .Parameters.Item(intField).Value = DBNull.Value
                                        End If
                                        intField += 1
                                        If IsNumeric(varRes(intCount + 1 + intResCount)) Then .Parameters.Item(intField).Value = varRes(intCount + 1 + intResCount)
                                        intField += 1
                                   Loop
                                   intCount = intCount + 2 + intResults
                                   intValCount += 1
                              Loop Until intValCount = intVals

                              For Each param As DbParameter In insert.Parameters
                                   ' Explicitly set the size of each parameter to make the Prepare command happy.
                                   If Len(param.Value) > 0 Then param.Size = Len(param.Value)
                                   ' Fill all remaining parameters with DBNulls.
                                   If param.Value Is Nothing Then param.Value = DBNull.Value
                              Next
                              ' Try it out!
                              Try
                                   .Prepare()
                                   Dim unused = .ExecuteNonQuery()
                                   SendCommData(ResultAcceptance)
                              Catch ex As FormatException
                                   ' If some value wasn't able to be cast as its expected data type, log enough information to figure out why.
                                   HandleError(ex)
                                   Dim sParams As String = ""
                                   For i As Integer = 0 To .Parameters.Count - 1
                                        sParams &= .Parameters(i).ParameterName & "," & .Parameters(i).DbType & ":" & .Parameters(i).Value & ";"
                                   Next
                                   AppendToLog("Parameters: " & sParams)
                                   SendCommData(Chr(2) & "M" & Chr(28) & "R" & Chr(28) & "1" & Chr(28) & "24" & Chr(3)) ' Result Reject Message.
#Disable Warning CA1031 ' Do not catch general exception types
#Disable Warning CA1031 ' Do not catch general exception types
                              Catch ex As Exception
#Enable Warning CA1031 ' Do not catch general exception types
                                   ' If it doesn't work, record the error and reject the result.
                                   ' We'll have 12 minutes to deal with it before a DMW/Host Communication Error is thrown.
                                   HandleError(ex)
                                   SendCommData(Chr(2) & "M" & Chr(28) & "R" & Chr(28) & "1" & Chr(28) & "24" & Chr(3)) ' Result Reject Message.
#Enable Warning CA1031 ' Do not catch general exception types
                              End Try
                         End With
                    End Using
               End Using
               Return 0
#Disable Warning CA1031 ' Do not catch general exception types
#Disable Warning CA1031 ' Do not catch general exception types
          Catch ex As Exception
#Enable Warning CA1031 ' Do not catch general exception types
               HandleError(ex)
               SendCommData(ResultAcceptance(False))
               Return ex.LineNumber()
#Enable Warning CA1031 ' Do not catch general exception types
          End Try
     End Function
     Function ResultMessage(ByVal strData As String)
          ' Receive a Result message. Then send a ResultAcceptance message, either accepting or rejecting it. Most likely accepting.
          Dim varRes = Split(strData, Chr(28), , vbBinaryCompare)
          Dim strSQL1 As String = "INSERT INTO SampleData ( "
          Dim strSQL2 As String = " ) VALUES ( "
          Dim objID As Object, intRes As Integer, intID As Integer
          Try

               Using cnSQL As IDbConnection = GetConnected()
                    Using insert As DbCommand = cnSQL.CreateCommand()
                         Dim fieldArray() As String = {"Patient_ID", "Sample_No", "SampleType", "Location", "Priority", "DateTime", "Cups", "Dilution", "TestsCount"}
                         For i = 0 To UBound(fieldArray)
                              If Len(varRes(i + 2)) > 0 Then
                                   insert.AddWithValue("@" & fieldArray(i), varRes(i + 2), DbType.String)
                                   strSQL1 &= fieldArray(i) & ", "
                                   strSQL2 &= "@" & fieldArray(i) & ", "
                              End If
                         Next
                         strSQL1 = Mid(strSQL1, 1, Len(strSQL1) - 2)
                         strSQL2 = Mid(strSQL2, 1, Len(strSQL2) - 2)
                         For Each param As DbParameter In insert.Parameters
                              If Len(param.Value) > 0 Then param.Size = Len(param.Value)
                         Next
#Disable Warning CA2100 ' Review SQL queries for security vulnerabilities
                         ' These strings are all pre-defined in the code above but selected based on which fields contain non-null values.
                         insert.CommandText = strSQL1 & strSQL2 & " )" ' This is all coming from the values in the fieldArray list, so no user input here.
#Enable Warning CA2100 ' Review SQL queries for security vulnerabilities
                         insert.Prepare()
                         intRes = insert.ExecuteNonQuery()
                    End Using
                    Using getNew As DbCommand = cnSQL.CreateCommand()
                         getNew.CommandText = "SELECT Max(SampleData.ID) AS MaxOfID FROM SampleData"
                         objID = getNew.ExecuteScalar()
                         intID = CInt(objID)
                    End Using
                    Dim str1 As String = "INSERT INTO SampleResults ( Sample_ID, TestName"
                    Dim str2 As String = " ) VALUES ( @Sample_ID, @TestName"
                    Dim intCount As Integer

                    Do Until intCount = CInt(varRes(10))
                         Using insert As DbCommand = cnSQL.CreateCommand()
                              str1 = "INSERT INTO SampleResults ( Sample_ID, TestName"
                              str2 = " ) VALUES ( @Sample_ID, @TestName"
                              insert.AddWithValue("@Sample_ID", intID, DbType.Int32)
                              insert.AddWithValue("@TestName", varRes(11 + intCount * 4), DbType.String)
                              If Len(varRes(12 + intCount * 4)) > 0 Then
                                   str1 &= ", ResultValue"
                                   str2 &= ", @ResultValue"
                                   insert.AddParam("@ResultValue", DbType.String)
                                   insert.Parameters.Item("@ResultValue").Value = varRes(12 + intCount * 4)
                              End If
                              If Len(varRes(13 + intCount * 4)) > 0 Then
                                   str1 &= ", Units"
                                   str2 &= ", @Units"
                                   insert.AddParam("@Units", DbType.String)
                                   insert.Parameters.Item("@Units").Value = varRes(13 + intCount * 4)
                              End If
                              If Len(varRes(14 + intCount * 4)) > 0 Then
                                   str1 &= ", Error "
                                   str2 &= ", @Error "
                                   insert.AddParam("@Error", DbType.String)
                                   insert.Parameters.Item("@Error").Value = varRes(14 + intCount * 4)
                              End If
                              For Each param As DbParameter In insert.Parameters
                                   If Len(param.Value) > 0 Then param.Size = Len(param.Value)
                              Next
                              insert.CommandText = str1 & str2 & " )"
                              insert.Prepare()
                              intRes = insert.ExecuteNonQuery()
                              intCount += 1

                         End Using
                    Loop
               End Using

               SendCommData(ResultAcceptance)
               Return 0
#Disable Warning CA1031 ' Do not catch general exception types
          Catch ex As Exception
               HandleError(ex)
               SendCommData(ResultAcceptance(False))
               Return ex.LineNumber()
#Enable Warning CA1031 ' Do not catch general exception types
          End Try
     End Function

     Shared Sub AppendToLog(strText As String)
          Static ci As System.Globalization.CultureInfo = System.Globalization.CultureInfo.GetCultureInfo("en-US")
          Dim strName As String = Environ("AllUsersProfile") & "\Rosie_Serial_LIS\Serial_Logs\SerialLog_" & Today.ToString("yyyy-MM-dd", ci.DateTimeFormat) & ".txt"
          If Directory.Exists(Environ("AllUsersProfile") & "\Rosie_Serial_LIS\Serial_Logs\") = False Then
               Directory.CreateDirectory(Environ("AllUsersProfile") & "\Rosie_Serial_LIS\Serial_Logs\")
          End If
          strText = Now() & " " & strText & vbCrLf
          File.AppendAllText(strName, strText)
     End Sub

     Shared Function NoRequestMessage() As String
          ' This function returns a string indicating to the instrument that the computer has no pending requests.
          Dim strHex As String
          strHex = Chr(2) & "N" & Chr(28)
          strHex = strHex & CHKSum(strHex) & Chr(3)
          Return strHex
     End Function

     Shared Function SampleRequestMessage(boolDelete As Boolean, strPatientID As String, strSampleNo As String, strSampleType As String, intPriority As Integer, ByRef strTests() As String, Optional iDilFactor As Integer = 1) As String
          ' This function returns a string to tell the instrument what tests to run on a sample.
          Dim strOut As String
          Dim intTests As Integer, intCount As Integer
          If strTests Is Nothing Or UBound(strTests) = 0 Then
               Throw New ArgumentNullException(NameOf(strTests))
          End If
          ' Count how many tests we need to add.
          intTests = UBound(strTests, 1) + 1
          intCount = 0
          strOut = Chr(2) & "D" & Chr(28) & "0" & Chr(28) & "0" & Chr(28)
          If boolDelete Then
               strOut &= "D"
          Else
               strOut &= "A"
          End If
          strOut &= Chr(28) & strPatientID & Chr(28) & strSampleNo & Chr(28) ' Message type, carrier ID, loadlist ID, Add/Delete, Patient ID, Sample ID.
          strOut &= strSampleType & Chr(28) & "" & Chr(28) & intPriority & Chr(28) & "1" & Chr(28) ' Sample type, location, priority, and # of cups for the sample.
          strOut &= "**" & Chr(28) & iDilFactor & Chr(28) ' Sample position (always "**" since positions have to be assigned on the instrument) and dilution factor.
          strOut &= intTests & Chr(28) ' The number of tests requested.
          Do
               strOut &= StrConv(strTests(intCount), vbUpperCase) & Chr(28)
               intCount += 1
          Loop Until intCount = intTests

          strOut &= CHKSum(strOut) & Chr(3)
          SampleRequestMessage = strOut

     End Function

     Public Shared Function Nz(ByVal Value As Object, Optional ByVal oDefault As Object = "") As Object
          If Value Is Nothing OrElse IsDBNull(Value) Then
               Return oDefault
          Else
               Return Value
          End If
     End Function

End Class
