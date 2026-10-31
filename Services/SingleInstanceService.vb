Imports System.IO
Imports System.IO.Pipes
Imports System.Security.Cryptography
Imports System.Text
Imports System.Text.Json
Imports System.Threading
Imports System.Threading.Channels

Namespace Services
    Public NotInheritable Class SingleInstanceService
        Implements IDisposable

        Private ReadOnly _name As String
        Private ReadOnly _mutex As Mutex
        Private ReadOnly _stop As New CancellationTokenSource()
        Private ReadOnly _requests As Channel(Of String()) = Channel.CreateUnbounded(Of String())()
        Private _ownsMutex As Boolean
        Private _listener As Task
        Private Declare Function GetNamedPipeServerProcessId Lib "kernel32.dll" (pipe As IntPtr, ByRef processId As UInteger) As Boolean
        Private Declare Function AllowSetForegroundWindow Lib "user32.dll" (processId As UInteger) As Boolean

        Public Sub New(profileDirectory As String)
            Dim identity = Path.GetFullPath(profileDirectory).TrimEnd(Path.DirectorySeparatorChar)
            If OperatingSystem.IsWindows() Then identity = identity.ToUpperInvariant()
            identity = Environment.UserName & "|" & identity
            _name = "FerrumPix-" & Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))
            _mutex = New Mutex(False, _name)
        End Sub

        ' Read only this flag before touching settings services, database or UI initialization.
        Public Shared Function IsEnabled(profileDirectory As String) As Boolean
            Dim settingsFile = Path.Combine(profileDirectory, "FerrumPix", "settings.json")
            Try
                If Not File.Exists(settingsFile) Then Return True
                Using json = JsonDocument.Parse(File.ReadAllText(settingsFile))
                    Dim value As JsonElement
                    If json.RootElement.TryGetProperty("OpenInExistingWindow", value) AndAlso
                       value.ValueKind = JsonValueKind.False Then Return False
                End Using
            Catch ex As IOException
            Catch ex As JsonException
            Catch ex As UnauthorizedAccessException
            End Try
            Return True
        End Function

        Public Function ForwardOrListen(arguments As String()) As Boolean
            Try
                _ownsMutex = _mutex.WaitOne(0)
            Catch ex As AbandonedMutexException
                _ownsMutex = True
            End Try
            If _ownsMutex Then
                _listener = Task.Run(AddressOf ListenAsync)
                Return False
            End If
            ' A bounded wait covers a simultaneous first launch before the server is ready.
            Try
                ForwardAsync(arguments).GetAwaiter().GetResult()
            Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is OperationCanceledException OrElse TypeOf ex Is TimeoutException
                ' Do not lose the user's file if the old process is hung or exiting.
                DiagnosticLogService.LogException("SingleInstance.Forward", ex)
                Return False
            End Try
            StartupTimingService.Mark("Forwarded to existing window")
            Return True
        End Function

        Private Async Function ForwardAsync(arguments As String()) As Task
            Using deadline As New CancellationTokenSource(TimeSpan.FromSeconds(5)),
                  pipe As New NamedPipeClientStream(".", _name, PipeDirection.InOut, PipeOptions.Asynchronous Or PipeOptions.CurrentUserOnly)
                Await pipe.ConnectAsync(deadline.Token)
                If OperatingSystem.IsWindows() Then
                    Dim serverId As UInteger
                    If GetNamedPipeServerProcessId(pipe.SafePipeHandle.DangerousGetHandle(), serverId) Then AllowSetForegroundWindow(serverId)
                End If
                Dim bytes = JsonSerializer.SerializeToUtf8Bytes(arguments)
                If bytes.Length > 65536 Then Throw New IOException("Open request is too large.")
                Await pipe.WriteAsync(BitConverter.GetBytes(bytes.Length), deadline.Token)
                Await pipe.WriteAsync(bytes, deadline.Token)
                Dim ack(0) As Byte
                Await pipe.ReadExactlyAsync(ack, deadline.Token)
                If ack(0) <> 1 Then Throw New IOException("Open request was not accepted.")
            End Using
        End Function

        Private Async Function ListenAsync() As Task
            While Not _stop.IsCancellationRequested
                Try
                    Using pipe As New NamedPipeServerStream(_name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                                                          PipeOptions.Asynchronous Or PipeOptions.CurrentUserOnly)
                        Await pipe.WaitForConnectionAsync(_stop.Token)
                        Using deadline = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token)
                            deadline.CancelAfter(TimeSpan.FromSeconds(5))
                            Dim header(3) As Byte
                            Await pipe.ReadExactlyAsync(header, deadline.Token)
                            Dim size = BitConverter.ToInt32(header)
                            If size < 1 OrElse size > 65536 Then Throw New IOException("Invalid open request size.")
                            Dim bytes(size - 1) As Byte
                            Await pipe.ReadExactlyAsync(bytes, deadline.Token)
                            Dim arguments = JsonSerializer.Deserialize(Of String())(bytes)
                            If arguments Is Nothing Then Throw New IOException("Invalid open request.")
                            Await _requests.Writer.WriteAsync(arguments, _stop.Token)
                            Await pipe.WriteAsync(New Byte() {1}, deadline.Token)
                        End Using
                    End Using
                Catch ex As OperationCanceledException
                Catch ex As IOException
                    If Not _stop.IsCancellationRequested Then DiagnosticLogService.LogException("SingleInstance.Receive", ex)
                Catch ex As JsonException
                    DiagnosticLogService.LogException("SingleInstance.Receive", ex)
                End Try
            End While
        End Function

        ' The UI attaches once ready. Requests received during startup remain queued.
        Public Async Function ProcessRequestsAsync(handler As Func(Of String(), Task)) As Task
            Try
                While Await _requests.Reader.WaitToReadAsync(_stop.Token)
                    Dim arguments As String() = Nothing
                    While _requests.Reader.TryRead(arguments)
                        Try
                            Await handler(arguments)
                        Catch ex As Exception
                            DiagnosticLogService.LogException("SingleInstance.Open", ex)
                        End Try
                    End While
                End While
            Catch ex As OperationCanceledException
            End Try
        End Function

        Public Sub Dispose() Implements IDisposable.Dispose
            _stop.Cancel()
            _listener?.GetAwaiter().GetResult()
            _requests.Writer.TryComplete()
            If _ownsMutex Then _mutex.ReleaseMutex()
            _mutex.Dispose()
        End Sub
    End Class
End Namespace
