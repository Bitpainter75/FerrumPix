Imports System
Imports System.Collections.Generic
Imports System.Diagnostics
Imports System.IO
Imports System.Linq
Imports System.Runtime.InteropServices
Imports System.Threading

Namespace Services

    ''' <summary>Ein Speicher eines MTP-Geraets, etwa der interne Speicher eines Telefons oder
    ''' seine Speicherkarte.</summary>
    Public Class MtpStorage
        Public Property DeviceName As String = ""
        Public Property StorageName As String = ""
        ''' <summary>Der D-Bus-Pfad des Speichers bei kmtpd.</summary>
        Public Property ObjectPath As String = ""

        ''' <summary>"mtp:/Geraet/Speicher", dieselbe Schreibweise wie im Dateimanager von KDE.</summary>
        Public ReadOnly Property Address As String
            Get
                Return MtpSession.AddressPrefix & DeviceName & "/" & StorageName
            End Get
        End Property
    End Class

    ''' <summary>Ein Eintrag in einem Ordner auf dem Geraet.</summary>
    Public Class MtpEntry
        Public Property Name As String = ""
        Public Property Size As Long
        Public Property ModifiedAt As DateTime
        Public Property IsFolder As Boolean
    End Class

    ''' <summary>
    ''' Zugang zu MTP-Geraeten (Telefone, manche Kameras) ueber kmtpd, den MTP-Dienst von KDE.
    '''
    ''' Unter KDE haelt kmtpd jedes MTP-Geraet, sobald es angesteckt ist, und haengt es nirgends als
    ''' Ordner ein; auch kio-fuse lehnt das Schema mtp ab. Der einzige Weg zu den Dateien ist damit
    ''' die D-Bus-Schnittstelle des Dienstes (org.kde.kmtpd5): Geraete, Speicher, Ordnerinhalte und
    ''' das Kopieren einer Datei in einen Dateideskriptor. Unter GNOME und verwandten Oberflaechen
    ''' haengt gvfs die Geraete dagegen als Ordner ein, die braucht diese Klasse nicht.
    '''
    ''' Gesprochen wird ueber libdbus-1 direkt: die D-Bus-Bibliothek, die Avalonia mitbringt, arbeitet
    ''' mit ref structs, und die kann VB.NET nicht verwenden. Jede Sitzung hat ihre eigene,
    ''' private Verbindung zum Sitzungsbus und gehoert EINEM Faden.
    '''
    ''' Angefasst wird der Dienst nur, wenn er schon laeuft oder die Sitzung eine KDE-Sitzung ist.
    ''' Sonst wuerde ein Aufruf ihn starten, und er naehme gvfs das Geraet weg.
    ''' </summary>
    Public NotInheritable Class MtpSession
        Implements IDisposable

        Public Const AddressPrefix As String = "mtp:/"

        Private Const ServiceName As String = "org.kde.kmtpd5"
        Private Const DaemonPath As String = "/modules/kmtpd"
        Private Const DaemonInterface As String = "org.kde.kmtp.Daemon"
        Private Const DeviceInterface As String = "org.kde.kmtp.Device"
        Private Const StorageInterface As String = "org.kde.kmtp.Storage"
        Private Const PropertiesInterface As String = "org.freedesktop.DBus.Properties"

        ''' Kurze Aufrufe; ein Ordner mit tausenden Aufnahmen braucht ueber MTP deutlich laenger.
        Private Const ShortTimeoutMs As Integer = 10000
        Private Const ListTimeoutMs As Integer = 300000
        ''' Kommt waehrend einer Kopie so lange weder Fortschritt noch Abschluss, gilt sie als haengend.
        Private Shared ReadOnly CopyStallLimit As TimeSpan = TimeSpan.FromMinutes(2)

        Private _connection As IntPtr
        Private _storages As List(Of MtpStorage)

        Private Sub New(connection As IntPtr)
            _connection = connection
        End Sub

        Public Shared Function IsAddress(text As String) As Boolean
            Return Not String.IsNullOrEmpty(text) AndAlso text.StartsWith(AddressPrefix, StringComparison.OrdinalIgnoreCase)
        End Function

        ''' <summary>Zerlegt "mtp:/Geraet/Speicher/DCIM/Camera" in Geraet, Speicher und den Pfad auf
        ''' dem Speicher ("/DCIM/Camera"). False, wenn Geraet oder Speicher fehlen.</summary>
        Public Shared Function TrySplitAddress(address As String, ByRef deviceName As String, ByRef storageName As String, ByRef storagePath As String) As Boolean
            deviceName = ""
            storageName = ""
            storagePath = "/"
            If Not IsAddress(address) Then Return False
            Dim parts = address.Substring(AddressPrefix.Length).Split("/"c).Where(Function(p) p.Length > 0).ToArray()
            If parts.Length < 2 Then Return False
            deviceName = parts(0)
            storageName = parts(1)
            storagePath = "/" & String.Join("/", parts.Skip(2))
            Return True
        End Function

        ''' <summary>Eine Sitzung, oder Nothing, wenn es keinen erreichbaren MTP-Dienst gibt (kein
        ''' Linux, kein libdbus, kein Bus, Dienst weder laufend noch in einer KDE-Sitzung, im Flatpak
        ''' ohne Freigabe fuer den Dienst).</summary>
        Public Shared Function TryOpen() As MtpSession
            If Not OperatingSystem.IsLinux() Then Return Nothing
            Dim connection = IntPtr.Zero
            Try
                Using err As New NativeError()
                    connection = dbus_bus_get_private(BusTypeSession, err.Handle)
                    If connection = IntPtr.Zero Then Return Nothing
                    ' Ab Werk beendet libdbus den ganzen Prozess, wenn der Bus verschwindet.
                    dbus_connection_set_exit_on_disconnect(connection, 0)
                    If dbus_connection_can_send_type(connection, TypeUnixFd) = 0 Then
                        Close(connection)
                        Return Nothing
                    End If
                    Dim running = dbus_bus_name_has_owner(connection, ServiceName, err.Handle) <> 0
                    If Not running AndAlso Not IsKdeSession() Then
                        Close(connection)
                        Return Nothing
                    End If
                End Using
                Return New MtpSession(connection)
            Catch ex As Exception When TypeOf ex Is DllNotFoundException OrElse TypeOf ex Is EntryPointNotFoundException
                Return Nothing
            Catch ex As Exception
                DiagnosticLogService.LogException("Mtp.Open", ex)
                If connection <> IntPtr.Zero Then Close(connection)
                Return Nothing
            End Try
        End Function

        Private Shared Function IsKdeSession() As Boolean
            Dim desktop = If(Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP"), "")
            Return desktop.Split(":"c).Any(Function(d) String.Equals(d, "KDE", StringComparison.OrdinalIgnoreCase))
        End Function

        ''' <summary>Alle Speicher aller angeschlossenen Geraete. Leer, wenn der Dienst nicht antwortet.
        ''' Tragen zwei Geraete denselben Namen (zweimal dasselbe Telefonmodell) oder ein Geraet zwei
        ''' gleich benannte Speicher, bekommt der Name das Ende des D-Bus-Pfads angehaengt, etwa
        ''' "Pixel 8 (device1)": die Adresse besteht nur aus Namen und muss genau einen Speicher treffen.</summary>
        Public Function ListStorages() As List(Of MtpStorage)
            If _storages IsNot Nothing Then Return _storages
            Dim result As New List(Of MtpStorage)()
            Try
                Dim devices As New List(Of (Path As String, Name As String))()
                For Each device In ReadObjectPaths(DaemonPath, DaemonInterface, "listDevices")
                    Dim deviceName = CleanName(ReadStringProperty(device, DeviceInterface, "friendlyName"))
                    If deviceName.Length = 0 Then deviceName = PathTail(device)
                    devices.Add((device, deviceName))
                Next
                For Each device In devices
                    Dim deviceName = device.Name
                    If devices.Where(Function(d) d.Name = device.Name).Count() > 1 Then deviceName &= " (" & PathTail(device.Path) & ")"
                    Dim storages As New List(Of (Path As String, Name As String))()
                    For Each storage In ReadObjectPaths(device.Path, DeviceInterface, "listStorages")
                        Dim storageName = CleanName(ReadStringProperty(storage, StorageInterface, "description"))
                        If storageName.Length = 0 Then storageName = PathTail(storage)
                        storages.Add((storage, storageName))
                    Next
                    For Each storage In storages
                        Dim storageName = storage.Name
                        If storages.Where(Function(s) s.Name = storage.Name).Count() > 1 Then storageName &= " (" & PathTail(storage.Path) & ")"
                        result.Add(New MtpStorage With {.DeviceName = deviceName, .StorageName = storageName, .ObjectPath = storage.Path})
                    Next
                Next
            Catch ex As Exception
                DiagnosticLogService.LogException("Mtp.ListStorages", ex)
            End Try
            _storages = result
            Return result
        End Function

        Private Shared Function PathTail(objectPath As String) As String
            Return objectPath.Substring(objectPath.LastIndexOf("/"c) + 1)
        End Function

        ''' <summary>Ein Schraegstrich im Namen wuerde die Adresse zerlegen.</summary>
        Private Shared Function CleanName(name As String) As String
            Return If(name, "").Replace("/"c, "_"c).Trim()
        End Function

        ''' <summary>Findet zu einer Adresse den Speicher und den Pfad darauf.</summary>
        Public Function TryResolve(address As String, ByRef storage As MtpStorage, ByRef storagePath As String) As Boolean
            storage = Nothing
            Dim deviceName = "", storageName = ""
            If Not TrySplitAddress(address, deviceName, storageName, storagePath) Then Return False
            ' Trifft die Adresse mehr als einen Speicher, wird keiner genommen: lieber nichts lesen als
            ' vom falschen Geraet.
            Dim matches = ListStorages().Where(Function(s) s.DeviceName = deviceName AndAlso s.StorageName = storageName).ToList()
            If matches.Count <> 1 Then Return False
            storage = matches(0)
            Return True
        End Function

        ''' <summary>Der Inhalt eines Ordners auf dem Speicher, oder Nothing, wenn es ihn nicht gibt.
        ''' Mit <paramref name="quick"/> gilt das kurze Zeitlimit statt des langen fuer grosse Ordner.</summary>
        Public Function ListFolder(storage As MtpStorage, storagePath As String, Optional quick As Boolean = False) As List(Of MtpEntry)
            Dim reply = CallMethod(storage.ObjectPath, StorageInterface, "getFilesAndFolders", If(quick, ShortTimeoutMs, ListTimeoutMs),
                                   Sub(iter) AppendString(iter, storagePath))
            Try
                Dim entries As New List(Of MtpEntry)()
                Using top As New NativeIter(), list As New NativeIter(), item As New NativeIter()
                    If dbus_message_iter_init(reply, top.Handle) = 0 OrElse dbus_message_iter_get_arg_type(top.Handle) <> TypeArray Then
                        Throw New IOException("Unerwartete Antwort von kmtpd")
                    End If
                    dbus_message_iter_recurse(top.Handle, list.Handle)
                    While dbus_message_iter_get_arg_type(list.Handle) = TypeStruct
                        ' (uuustxs): Kennung, Elternkennung, Speicher, Name, Groesse, Aenderungszeit, Medientyp.
                        dbus_message_iter_recurse(list.Handle, item.Handle)
                        For skip = 1 To 3
                            dbus_message_iter_next(item.Handle)
                        Next
                        Dim name = ReadString(item.Handle)
                        dbus_message_iter_next(item.Handle)
                        Dim size = ReadInt64(item.Handle)
                        dbus_message_iter_next(item.Handle)
                        Dim modified = ReadInt64(item.Handle)
                        dbus_message_iter_next(item.Handle)
                        Dim mimeType = ReadString(item.Handle)
                        entries.Add(New MtpEntry With {
                            .Name = name, .Size = size, .IsFolder = mimeType = "inode/directory",
                            .ModifiedAt = DateTimeOffset.FromUnixTimeSeconds(modified).LocalDateTime})
                        dbus_message_iter_next(list.Handle)
                    End While
                    ' Danach das Ergebnis: 0 gefunden, 1 gibt es nicht, 2 ist eine Datei.
                    dbus_message_iter_next(top.Handle)
                    If dbus_message_iter_get_arg_type(top.Handle) = TypeInt32 AndAlso ReadInt32(top.Handle) <> 0 Then Return Nothing
                End Using
                Return entries
            Finally
                dbus_message_unref(reply)
            End Try
        End Function

        ''' <summary>Kopiert eine Datei vom Geraet in <paramref name="targetFile"/>, das es noch nicht
        ''' geben darf. Wirft bei einem Fehler; die halbe Datei raeumt der Aufrufer weg.</summary>
        Public Sub FetchFile(storage As MtpStorage, storagePath As String, targetFile As String, token As CancellationToken)
            Dim rule = "type='signal',interface='" & StorageInterface & "',path='" & storage.ObjectPath & "'"
            Using err As New NativeError()
                dbus_bus_add_match(_connection, rule, err.Handle)
                err.ThrowIfSet()
            End Using
            Try
                Using output As New FileStream(targetFile, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite)
                    Dim descriptor = CInt(output.SafeFileHandle.DangerousGetHandle().ToInt64())
                    ' libdbus verdoppelt den Deskriptor beim Anhaengen; kmtpd schreibt in seine Kopie.
                    Dim reply = CallMethod(storage.ObjectPath, StorageInterface, "getFileToFileDescriptor", ShortTimeoutMs,
                                           Sub(iter)
                                               AppendFileDescriptor(iter, descriptor)
                                               AppendString(iter, storagePath)
                                           End Sub)
                    Dim started As Integer
                    Try
                        started = ReadFirstInt32(reply)
                    Finally
                        dbus_message_unref(reply)
                    End Try
                    If started <> 0 Then Throw New IOException("kmtpd kann die Datei nicht lesen: " & storagePath)
                    WaitForCopy(storage, targetFile, token)
                End Using
            Finally
                dbus_bus_remove_match(_connection, rule, IntPtr.Zero)
            End Try
        End Sub

        ''' <summary>Wartet auf das Signal copyFinished des Speichers. Die Kopie laeuft in kmtpd erst
        ''' nach der Antwort auf den Aufruf; Fortschritt kommt als copyProgress.</summary>
        Private Sub WaitForCopy(storage As MtpStorage, targetFile As String, token As CancellationToken)
            Dim quiet = Stopwatch.StartNew()
            Dim lastLength = -1L
            While True
                token.ThrowIfCancellationRequested()
                Dim message = dbus_connection_pop_message(_connection)
                While message <> IntPtr.Zero
                    Try
                        If Marshal.PtrToStringUTF8(dbus_message_get_path(message)) = storage.ObjectPath Then
                            If dbus_message_is_signal(message, StorageInterface, "copyFinished") <> 0 Then
                                Dim code = ReadFirstInt32(message)
                                If code <> 0 Then Throw New IOException("kmtpd meldet einen Fehler beim Kopieren: " & code.ToString(Globalization.CultureInfo.InvariantCulture))
                                Return
                            End If
                            If dbus_message_is_signal(message, StorageInterface, "copyProgress") <> 0 Then quiet.Restart()
                        End If
                    Finally
                        dbus_message_unref(message)
                    End Try
                    message = dbus_connection_pop_message(_connection)
                End While
                If dbus_connection_read_write(_connection, 250) = 0 Then Throw New IOException("Die Verbindung zu kmtpd ist abgerissen")
                Dim length = New FileInfo(targetFile).Length
                If length <> lastLength Then
                    lastLength = length
                    quiet.Restart()
                End If
                If quiet.Elapsed > CopyStallLimit Then Throw New IOException("Das Geraet antwortet nicht mehr")
            End While
        End Sub

        Public Sub Dispose() Implements IDisposable.Dispose
            If _connection = IntPtr.Zero Then Return
            Close(_connection)
            _connection = IntPtr.Zero
        End Sub

        Private Shared Sub Close(connection As IntPtr)
            Try
                dbus_connection_close(connection)
                dbus_connection_unref(connection)
            Catch
            End Try
        End Sub

#Region "Aufrufe und Werte"

        Private Function CallMethod(path As String, iface As String, method As String, timeoutMs As Integer, Optional appendArgs As Action(Of IntPtr) = Nothing) As IntPtr
            Dim message = dbus_message_new_method_call(ServiceName, path, iface, method)
            If message = IntPtr.Zero Then Throw New OutOfMemoryException()
            Try
                If appendArgs IsNot Nothing Then
                    Using iter As New NativeIter()
                        dbus_message_iter_init_append(message, iter.Handle)
                        appendArgs(iter.Handle)
                    End Using
                End If
                Using err As New NativeError()
                    Dim reply = dbus_connection_send_with_reply_and_block(_connection, message, timeoutMs, err.Handle)
                    err.ThrowIfSet()
                    If reply = IntPtr.Zero Then Throw New IOException("Keine Antwort von kmtpd")
                    Return reply
                End Using
            Finally
                dbus_message_unref(message)
            End Try
        End Function

        Private Function ReadObjectPaths(path As String, iface As String, method As String) As List(Of String)
            Dim reply = CallMethod(path, iface, method, ShortTimeoutMs)
            Try
                Dim result As New List(Of String)()
                Using top As New NativeIter(), list As New NativeIter()
                    If dbus_message_iter_init(reply, top.Handle) = 0 OrElse dbus_message_iter_get_arg_type(top.Handle) <> TypeArray Then Return result
                    dbus_message_iter_recurse(top.Handle, list.Handle)
                    While dbus_message_iter_get_arg_type(list.Handle) = TypeObjectPath
                        result.Add(ReadString(list.Handle))
                        dbus_message_iter_next(list.Handle)
                    End While
                End Using
                Return result
            Finally
                dbus_message_unref(reply)
            End Try
        End Function

        Private Function ReadStringProperty(path As String, iface As String, name As String) As String
            Dim reply = CallMethod(path, PropertiesInterface, "Get", ShortTimeoutMs,
                                   Sub(iter)
                                       AppendString(iter, iface)
                                       AppendString(iter, name)
                                   End Sub)
            Try
                Using top As New NativeIter(), value As New NativeIter()
                    If dbus_message_iter_init(reply, top.Handle) = 0 OrElse dbus_message_iter_get_arg_type(top.Handle) <> TypeVariant Then Return ""
                    dbus_message_iter_recurse(top.Handle, value.Handle)
                    If dbus_message_iter_get_arg_type(value.Handle) <> TypeString Then Return ""
                    Return ReadString(value.Handle)
                End Using
            Finally
                dbus_message_unref(reply)
            End Try
        End Function

        Private Shared Function ReadFirstInt32(message As IntPtr) As Integer
            Using iter As New NativeIter()
                If dbus_message_iter_init(message, iter.Handle) = 0 OrElse dbus_message_iter_get_arg_type(iter.Handle) <> TypeInt32 Then
                    Throw New IOException("Unerwartete Antwort von kmtpd")
                End If
                Return ReadInt32(iter.Handle)
            End Using
        End Function

        Private Shared Sub AppendString(iter As IntPtr, value As String)
            Dim text = Marshal.StringToCoTaskMemUTF8(value)
            Dim box = Marshal.AllocHGlobal(IntPtr.Size)
            Try
                Marshal.WriteIntPtr(box, text)
                If dbus_message_iter_append_basic(iter, TypeString, box) = 0 Then Throw New OutOfMemoryException()
            Finally
                Marshal.FreeHGlobal(box)
                Marshal.FreeCoTaskMem(text)
            End Try
        End Sub

        Private Shared Sub AppendFileDescriptor(iter As IntPtr, descriptor As Integer)
            Dim box = Marshal.AllocHGlobal(8)
            Try
                Marshal.WriteInt32(box, descriptor)
                If dbus_message_iter_append_basic(iter, TypeUnixFd, box) = 0 Then Throw New IOException("Dateideskriptor laesst sich nicht uebergeben")
            Finally
                Marshal.FreeHGlobal(box)
            End Try
        End Sub

        Private Shared Function ReadString(iter As IntPtr) As String
            Dim box = Marshal.AllocHGlobal(8)
            Try
                dbus_message_iter_get_basic(iter, box)
                Return If(Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(box)), "")
            Finally
                Marshal.FreeHGlobal(box)
            End Try
        End Function

        Private Shared Function ReadInt32(iter As IntPtr) As Integer
            Dim box = Marshal.AllocHGlobal(8)
            Try
                dbus_message_iter_get_basic(iter, box)
                Return Marshal.ReadInt32(box)
            Finally
                Marshal.FreeHGlobal(box)
            End Try
        End Function

        Private Shared Function ReadInt64(iter As IntPtr) As Long
            Dim box = Marshal.AllocHGlobal(8)
            Try
                dbus_message_iter_get_basic(iter, box)
                Return Marshal.ReadInt64(box)
            Finally
                Marshal.FreeHGlobal(box)
            End Try
        End Function

        ''' <summary>Ein DBusMessageIter. Die Struktur ist undurchsichtig und auf 64 Bit 72 Byte
        ''' gross; reserviert wird reichlich.</summary>
        Private NotInheritable Class NativeIter
            Implements IDisposable
            Public ReadOnly Handle As IntPtr = Marshal.AllocHGlobal(256)
            Public Sub Dispose() Implements IDisposable.Dispose
                Marshal.FreeHGlobal(Handle)
            End Sub
        End Class

        ''' <summary>Ein DBusError (Name, Text und Verwaltung, 32 Byte auf 64 Bit).</summary>
        Private NotInheritable Class NativeError
            Implements IDisposable
            Public ReadOnly Handle As IntPtr = Marshal.AllocHGlobal(64)

            Public Sub New()
                dbus_error_init(Handle)
            End Sub

            Public Sub ThrowIfSet()
                If dbus_error_is_set(Handle) = 0 Then Return
                Dim name = Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(Handle))
                Dim text = Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(Handle, IntPtr.Size))
                Throw New IOException(name & ": " & text)
            End Sub

            Public Sub Dispose() Implements IDisposable.Dispose
                dbus_error_free(Handle)
                Marshal.FreeHGlobal(Handle)
            End Sub
        End Class

#End Region

#Region "libdbus-1"

        Private Const DBusLibrary As String = "libdbus-1.so.3"
        Private Const BusTypeSession As Integer = 0
        Private Shared ReadOnly TypeInt32 As Integer = AscW("i"c)
        Private Shared ReadOnly TypeString As Integer = AscW("s"c)
        Private Shared ReadOnly TypeObjectPath As Integer = AscW("o"c)
        Private Shared ReadOnly TypeArray As Integer = AscW("a"c)
        Private Shared ReadOnly TypeStruct As Integer = AscW("r"c)
        Private Shared ReadOnly TypeVariant As Integer = AscW("v"c)
        Private Shared ReadOnly TypeUnixFd As Integer = AscW("h"c)

        <DllImport(DBusLibrary)>
        Private Shared Sub dbus_error_init(err As IntPtr)
        End Sub

        <DllImport(DBusLibrary)>
        Private Shared Function dbus_error_is_set(err As IntPtr) As Integer
        End Function

        <DllImport(DBusLibrary)>
        Private Shared Sub dbus_error_free(err As IntPtr)
        End Sub

        <DllImport(DBusLibrary)>
        Private Shared Function dbus_bus_get_private(busType As Integer, err As IntPtr) As IntPtr
        End Function

        <DllImport(DBusLibrary)>
        Private Shared Sub dbus_connection_set_exit_on_disconnect(connection As IntPtr, exitOnDisconnect As Integer)
        End Sub

        <DllImport(DBusLibrary)>
        Private Shared Function dbus_connection_can_send_type(connection As IntPtr, type As Integer) As Integer
        End Function

        <DllImport(DBusLibrary)>
        Private Shared Sub dbus_connection_close(connection As IntPtr)
        End Sub

        <DllImport(DBusLibrary)>
        Private Shared Sub dbus_connection_unref(connection As IntPtr)
        End Sub

        <DllImport(DBusLibrary)>
        Private Shared Function dbus_bus_name_has_owner(connection As IntPtr, <MarshalAs(UnmanagedType.LPUTF8Str)> name As String, err As IntPtr) As Integer
        End Function

        <DllImport(DBusLibrary)>
        Private Shared Sub dbus_bus_add_match(connection As IntPtr, <MarshalAs(UnmanagedType.LPUTF8Str)> rule As String, err As IntPtr)
        End Sub

        <DllImport(DBusLibrary)>
        Private Shared Sub dbus_bus_remove_match(connection As IntPtr, <MarshalAs(UnmanagedType.LPUTF8Str)> rule As String, err As IntPtr)
        End Sub

        <DllImport(DBusLibrary)>
        Private Shared Function dbus_message_new_method_call(<MarshalAs(UnmanagedType.LPUTF8Str)> destination As String,
                                                             <MarshalAs(UnmanagedType.LPUTF8Str)> path As String,
                                                             <MarshalAs(UnmanagedType.LPUTF8Str)> iface As String,
                                                             <MarshalAs(UnmanagedType.LPUTF8Str)> method As String) As IntPtr
        End Function

        <DllImport(DBusLibrary)>
        Private Shared Sub dbus_message_unref(message As IntPtr)
        End Sub

        <DllImport(DBusLibrary)>
        Private Shared Function dbus_connection_send_with_reply_and_block(connection As IntPtr, message As IntPtr, timeoutMs As Integer, err As IntPtr) As IntPtr
        End Function

        <DllImport(DBusLibrary)>
        Private Shared Function dbus_connection_read_write(connection As IntPtr, timeoutMs As Integer) As Integer
        End Function

        <DllImport(DBusLibrary)>
        Private Shared Function dbus_connection_pop_message(connection As IntPtr) As IntPtr
        End Function

        <DllImport(DBusLibrary)>
        Private Shared Function dbus_message_is_signal(message As IntPtr, <MarshalAs(UnmanagedType.LPUTF8Str)> iface As String,
                                                       <MarshalAs(UnmanagedType.LPUTF8Str)> name As String) As Integer
        End Function

        <DllImport(DBusLibrary)>
        Private Shared Function dbus_message_get_path(message As IntPtr) As IntPtr
        End Function

        <DllImport(DBusLibrary)>
        Private Shared Sub dbus_message_iter_init_append(message As IntPtr, iter As IntPtr)
        End Sub

        <DllImport(DBusLibrary)>
        Private Shared Function dbus_message_iter_append_basic(iter As IntPtr, type As Integer, value As IntPtr) As Integer
        End Function

        <DllImport(DBusLibrary)>
        Private Shared Function dbus_message_iter_init(message As IntPtr, iter As IntPtr) As Integer
        End Function

        <DllImport(DBusLibrary)>
        Private Shared Function dbus_message_iter_get_arg_type(iter As IntPtr) As Integer
        End Function

        <DllImport(DBusLibrary)>
        Private Shared Sub dbus_message_iter_get_basic(iter As IntPtr, value As IntPtr)
        End Sub

        <DllImport(DBusLibrary)>
        Private Shared Sub dbus_message_iter_recurse(iter As IntPtr, subIter As IntPtr)
        End Sub

        <DllImport(DBusLibrary)>
        Private Shared Function dbus_message_iter_next(iter As IntPtr) As Integer
        End Function

#End Region

    End Class

End Namespace
