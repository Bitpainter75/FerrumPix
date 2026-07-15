Imports System
Imports System.Runtime.InteropServices
Imports Avalonia

Namespace Services

    Public NotInheritable Class NativePointerService
        Private Sub New()
        End Sub

        Public Shared Function TryGetCursorPosition(ByRef position As PixelPoint) As Boolean
            Try
                If OperatingSystem.IsWindows() Then
                    Dim point As WinPoint
                    If GetCursorPos(point) Then
                        position = New PixelPoint(point.X, point.Y)
                        Return True
                    End If
                ElseIf OperatingSystem.IsLinux() Then
                    Return TryGetX11CursorPosition(position)
                End If
            Catch
            End Try

            position = New PixelPoint()
            Return False
        End Function

        Private Shared Function TryGetX11CursorPosition(ByRef position As PixelPoint) As Boolean
            Dim display = XOpenDisplay(IntPtr.Zero)
            If display = IntPtr.Zero Then Return False

            Try
                Dim root = XDefaultRootWindow(display)
                Dim rootReturn As IntPtr
                Dim childReturn As IntPtr
                Dim rootX As Integer
                Dim rootY As Integer
                Dim winX As Integer
                Dim winY As Integer
                Dim mask As UInteger
                If XQueryPointer(display, root, rootReturn, childReturn, rootX, rootY, winX, winY, mask) = 0 Then
                    Return False
                End If

                position = New PixelPoint(rootX, rootY)
                Return True
            Finally
                XCloseDisplay(display)
            End Try
        End Function

        <StructLayout(LayoutKind.Sequential)>
        Private Structure WinPoint
            Public X As Integer
            Public Y As Integer
        End Structure

        <DllImport("user32.dll", SetLastError:=True)>
        Private Shared Function GetCursorPos(ByRef point As WinPoint) As Boolean
        End Function

        <DllImport("libX11", EntryPoint:="XOpenDisplay")>
        Private Shared Function XOpenDisplay(displayName As IntPtr) As IntPtr
        End Function

        <DllImport("libX11", EntryPoint:="XDefaultRootWindow")>
        Private Shared Function XDefaultRootWindow(display As IntPtr) As IntPtr
        End Function

        <DllImport("libX11", EntryPoint:="XQueryPointer")>
        Private Shared Function XQueryPointer(display As IntPtr,
                                             window As IntPtr,
                                             ByRef rootReturn As IntPtr,
                                             ByRef childReturn As IntPtr,
                                             ByRef rootXReturn As Integer,
                                             ByRef rootYReturn As Integer,
                                             ByRef winXReturn As Integer,
                                             ByRef winYReturn As Integer,
                                             ByRef maskReturn As UInteger) As Integer
        End Function

        <DllImport("libX11", EntryPoint:="XCloseDisplay")>
        Private Shared Function XCloseDisplay(display As IntPtr) As Integer
        End Function
    End Class

End Namespace
