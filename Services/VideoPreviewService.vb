Imports System
Imports System.Collections.Generic
Imports System.Globalization
Imports System.IO
Imports System.Runtime.InteropServices
Imports System.Threading

Namespace Services

    ''' Extrahiert ein Standbild aus Videodateien für die Thumbnail-Pipeline über libmpv.
    ''' Pro Thumbnail wird ein kurzlebiger headless mpv-Handle verwendet; die Aufrufe werden seriell
    ''' gehalten, weil mehrere parallele Decoder schnell mehr Last erzeugen als der Thumbnail-Cache spart.
    Public Class VideoPreviewService

        Private Shared ReadOnly _videoExtensions As String() = {".mp4", ".mov", ".mkv", ".avi", ".webm", ".m4v"}
        Private Shared ReadOnly _thumbnailGate As New SemaphoreSlim(1, 1)

        Public Shared Function IsSupportedVideo(filePath As String) As Boolean
            If String.IsNullOrEmpty(filePath) Then Return False
            Return _videoExtensions.Contains(Path.GetExtension(filePath).ToLowerInvariant())
        End Function

        Public Shared Function ExtractPreview(filePath As String, Optional maxDimension As Integer = 480, Optional timeoutMs As Integer = 4000) As MemoryStream
            If Not App.IsVideoThumbnailAvailable Then Return Nothing
            If Not IsSupportedVideo(filePath) OrElse Not File.Exists(filePath) Then Return Nothing

            If Not _thumbnailGate.Wait(Math.Max(timeoutMs, 4000)) Then Return Nothing
            Try
                Return ExtractPreviewCore(filePath, maxDimension, timeoutMs)
            Finally
                _thumbnailGate.Release()
            End Try
        End Function

        Private Shared Function ExtractPreviewCore(filePath As String, maxDimension As Integer, timeoutMs As Integer) As MemoryStream
            Dim name = Path.GetFileName(filePath)
            Dim tempPath = Path.Combine(Path.GetTempPath(), $"ferrumpix_vthumb_{Guid.NewGuid():N}.png")
            Dim handle = IntPtr.Zero

            Try
                MpvInterop.EnsureResolver()
                handle = MpvInterop.Create()
                If handle = IntPtr.Zero Then Return Nothing

                SetOption(handle, "terminal", "no")
                ' FFmpeg-Demuxer-Hinweise ("Skipping unhandled metadata ...") komplett stumm -
                ' terminal=no allein laesst bei parallel lebenden mpv-Instanzen gelegentlich rohe
                ' libav-Meldungen auf die Konsole durch (kosmetisch, aber Spam).
                SetOption(handle, "msg-level", "all=no")
                SetOption(handle, "config", "no")
                SetOption(handle, "input-default-bindings", "no")
                SetOption(handle, "osc", "no")
                SetOption(handle, "audio", "no")
                SetOption(handle, "pause", "yes")
                SetOption(handle, "keep-open", "yes")
                SetOption(handle, "hwdec", "no")
                SetOption(handle, "vo", "null")

                Dim result = MpvInterop.Initialize(handle)
                If result < 0 Then
                    DiagnosticLogService.LogAlways("Video.Thumbnail", $"{name}: mpv_initialize fehlgeschlagen ({result})")
                    Return Nothing
                End If

                If Command(handle, "loadfile", filePath, "replace") < 0 Then
                    DiagnosticLogService.LogAlways("Video.Thumbnail", $"{name}: loadfile fehlgeschlagen")
                    Return Nothing
                End If

                If Not WaitForEvent(handle, timeoutMs, MpvInterop.MpvEventId.FileLoaded) Then
                    DiagnosticLogService.LogAlways("Video.Thumbnail", $"{name}: kein FileLoaded binnen {timeoutMs} ms")
                    Return Nothing
                End If

                Command(handle, "seek", "10", "absolute-percent", "exact")
                WaitForEvent(handle, Math.Min(timeoutMs, 1500), MpvInterop.MpvEventId.PlaybackRestart)

                If Command(handle, "screenshot-to-file", tempPath, "video") < 0 Then
                    DiagnosticLogService.LogAlways("Video.Thumbnail", $"{name}: screenshot-to-file fehlgeschlagen")
                    Return Nothing
                End If

                If Not WaitForFile(tempPath, timeoutMs) Then
                    DiagnosticLogService.LogAlways("Video.Thumbnail", $"{name}: kein Screenshot binnen {timeoutMs} ms")
                    Return Nothing
                End If

                Return ScalePngToMaxDimension(File.ReadAllBytes(tempPath), maxDimension)
            Catch ex As Exception
                DiagnosticLogService.LogException("Video.Thumbnail", ex)
                Return Nothing
            Finally
                If handle <> IntPtr.Zero Then
                    Try
                        MpvInterop.TerminateDestroy(handle)
                    Catch
                    End Try
                End If
                Try
                    If File.Exists(tempPath) Then File.Delete(tempPath)
                Catch
                End Try
            End Try
        End Function

        Private Shared Function WaitForEvent(handle As IntPtr, timeoutMs As Integer, ParamArray acceptedEvents As MpvInterop.MpvEventId()) As Boolean
            Dim accepted = New HashSet(Of MpvInterop.MpvEventId)(acceptedEvents)
            Dim deadline = Environment.TickCount64 + timeoutMs
            Do
                Dim remaining = deadline - Environment.TickCount64
                If remaining <= 0 Then Return False

                Dim eventPtr = MpvInterop.WaitEvent(handle, Math.Min(0.1, remaining / 1000.0))
                If eventPtr = IntPtr.Zero Then Continue Do

                Dim ev = Marshal.PtrToStructure(Of MpvInterop.MpvEvent)(eventPtr)
                If accepted.Contains(ev.EventId) Then Return ev.Error >= 0
                If ev.EventId = MpvInterop.MpvEventId.EndFile AndAlso accepted.Contains(MpvInterop.MpvEventId.FileLoaded) Then Return False
                If ev.EventId = MpvInterop.MpvEventId.Shutdown Then Return False
            Loop
        End Function

        Private Shared Function WaitForFile(path As String, timeoutMs As Integer) As Boolean
            Dim deadline = Environment.TickCount64 + timeoutMs
            Do
                If File.Exists(path) Then
                    Try
                        If New FileInfo(path).Length > 0 Then Return True
                    Catch
                    End Try
                End If
                If Environment.TickCount64 >= deadline Then Return False
                Thread.Sleep(50)
            Loop
        End Function

        Private Shared Function ScalePngToMaxDimension(pngBytes As Byte(), maxDimension As Integer) As MemoryStream
            If maxDimension <= 0 Then Return New MemoryStream(pngBytes)

            Try
                Using source = SkiaSharp.SKBitmap.Decode(pngBytes)
                    If source Is Nothing Then Return New MemoryStream(pngBytes)
                    Dim longest = Math.Max(source.Width, source.Height)
                    If longest <= maxDimension Then Return New MemoryStream(pngBytes)

                    Dim scale = maxDimension / CDbl(longest)
                    Dim width = Math.Max(1, CInt(Math.Round(source.Width * scale)))
                    Dim height = Math.Max(1, CInt(Math.Round(source.Height * scale)))
                    Using resized = source.Resize(New SkiaSharp.SKImageInfo(width, height), SkiaSharp.SKSamplingOptions.Default)
                        If resized Is Nothing Then Return New MemoryStream(pngBytes)
                        Dim outStream As New MemoryStream()
                        Using img = SkiaSharp.SKImage.FromBitmap(resized)
                            Using data = img.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100)
                                data.SaveTo(outStream)
                            End Using
                        End Using
                        outStream.Position = 0
                        Return outStream
                    End Using
                End Using
            Catch
                Return New MemoryStream(pngBytes)
            End Try
        End Function

        Private Shared Sub SetOption(handle As IntPtr, name As String, value As String)
            Using namePtr = New Utf8String(name), valuePtr = New Utf8String(value)
                Dim result = MpvInterop.SetOptionString(handle, namePtr.Pointer, valuePtr.Pointer)
                If result < 0 Then Throw New InvalidOperationException($"libmpv option {name} fehlgeschlagen ({result}).")
            End Using
        End Sub

        Private Shared Function Command(handle As IntPtr, ParamArray args As String()) As Integer
            Dim allocations As New List(Of Utf8String)()
            Dim ptrs As New List(Of IntPtr)()
            Try
                For Each arg In args
                    Dim utf8 = New Utf8String(arg)
                    allocations.Add(utf8)
                    ptrs.Add(utf8.Pointer)
                Next
                ptrs.Add(IntPtr.Zero)

                Dim arrayPtr = Marshal.AllocHGlobal(IntPtr.Size * ptrs.Count)
                Try
                    For i = 0 To ptrs.Count - 1
                        Marshal.WriteIntPtr(arrayPtr, i * IntPtr.Size, ptrs(i))
                    Next
                    Return MpvInterop.Command(handle, arrayPtr)
                Finally
                    Marshal.FreeHGlobal(arrayPtr)
                End Try
            Finally
                For Each allocation In allocations
                    allocation.Dispose()
                Next
            End Try
        End Function

        Private NotInheritable Class Utf8String
            Implements IDisposable

            Public ReadOnly Property Pointer As IntPtr

            Public Sub New(value As String)
                Dim bytes = Text.Encoding.UTF8.GetBytes(If(value, String.Empty) & ChrW(0))
                Pointer = Marshal.AllocHGlobal(bytes.Length)
                Marshal.Copy(bytes, 0, Pointer, bytes.Length)
            End Sub

            Public Sub Dispose() Implements IDisposable.Dispose
                If Pointer <> IntPtr.Zero Then Marshal.FreeHGlobal(Pointer)
            End Sub
        End Class

    End Class

End Namespace
