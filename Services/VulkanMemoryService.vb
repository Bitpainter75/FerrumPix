Imports System
Imports System.Collections.Generic
Imports System.Linq
Imports System.Runtime.InteropServices

Namespace Services

    ''' <summary>Wie viel eigenen Speicher eine Grafikkarte hat - gefragt wird Vulkan selbst.
    '''
    ''' WOFUER. Die Kachelgroesse des Entrauschens entscheidet ueber den Speicherbedarf auf der
    ''' Karte, und der ist gemessen: eine 512er Kachel braucht rund 2,3 GB, eine 256er unter 900 MB.
    ''' Auf einer Karte mit 2 GB scheitert die grosse Kachel deshalb, und je nach Treiber nicht mit
    ''' einer Fehlermeldung, sondern mit einem Abbruch des ganzen Prozesses: die Ausnahme faellt
    ''' unter uns in der nativen Laufzeit und ruft dort terminate. Abfangen laesst sich das nicht,
    ''' nur vermeiden - und dafuer muss die Groesse des Speichers bekannt sein.
    '''
    ''' WARUM VULKAN UND NICHTS ANDERES. Weder .NET noch Avalonia noch Skia nennen die Kapazitaet
    ''' einer Karte: Skia kennt nur seinen EIGENEN Zwischenspeicher (GetResourceCacheLimit), und
    ''' Avalonias IPlatformGraphics hat gar keinen Adapterbegriff. Und selbst wenn - es waere die
    ''' Karte, auf der die OBERFLAECHE zeichnet, und die muss nicht die sein, auf der gerechnet wird
    ''' (siehe die Auswahl in <see cref="GpuAccelerationService"/>). Vulkan ist zugleich die
    ''' Schicht, auf der der Rechenanbieter selbst sitzt, die Antwort passt also zur Sache.
    '''
    ''' NUR WINDOWS UND LINUX. Unter macOS gibt es kein natives Vulkan, dort rechnet der Anbieter
    ''' ueber Metal. Das Gegenstueck waere MTLDevice.recommendedMaxWorkingSetSize; es fehlt hier
    ''' bewusst, weil der Fall dort nicht auftritt: Apple-Silicon teilt sich den Arbeitsspeicher und
    ''' liegt weit ueber dem, was die grosse Kachel braucht.
    '''
    ''' SCHEITERN IST ERLAUBT. Fehlt der Lader, kennt niemand die Karte oder antwortet Vulkan nicht,
    ''' kommt 0 zurueck und der Aufrufer bleibt beim bisherigen Verhalten. Ohne Lader gibt es
    ''' ohnehin keine Beschleunigung, die Frage stellt sich dann gar nicht.</summary>
    Public NotInheritable Class VulkanMemoryService

        Private Sub New()
        End Sub

        Private Const VkStructureTypeInstanceCreateInfo As Integer = 1
        ''' Der Speicherhaufen gehoert der Karte selbst - VK_MEMORY_HEAP_DEVICE_LOCAL_BIT.
        Private Const HeapDeviceLocal As ULong = 1UL

        Private Delegate Function CreateInstanceFn(createInfo As IntPtr, allocator As IntPtr, ByRef instance As IntPtr) As Integer
        Private Delegate Function EnumerateDevicesFn(instance As IntPtr, ByRef count As UInteger, devices As IntPtr()) As Integer
        Private Delegate Sub DevicePropertiesFn(device As IntPtr, properties As Byte())
        Private Delegate Sub MemoryPropertiesFn(device As IntPtr, properties As Byte())
        Private Delegate Sub DestroyInstanceFn(instance As IntPtr, allocator As IntPtr)

        ''' <summary>Was Vulkan ueber eine Karte sagt.</summary>
        Public NotInheritable Class VulkanDevice
            ''' Derselbe Schluessel, den GpuAccelerationService fuehrt: Hersteller und Geraet, hex.
            Public Property Key As String = ""
            Public Property Name As String = ""
            ''' Der groesste kartenEIGENE Speicherhaufen, in Mebibyte. 0 heisst unbekannt.
            Public Property DeviceLocalMiB As Integer
        End Class

        Private Shared ReadOnly _lock As New Object()
        Private Shared _checked As Boolean = False
        Private Shared _devices As New List(Of VulkanDevice)()

        ''' <summary>Alle Karten, die Vulkan nennt. Leer, wenn nicht gefragt werden konnte. Die
        ''' Abfrage laeuft EINMAL je Sitzung: sie legt eine Vulkan-Instanz an, und das kostet.</summary>
        Public Shared ReadOnly Property Devices As IReadOnlyList(Of VulkanDevice)
            Get
                SyncLock _lock
                    If Not _checked Then
                        _checked = True
                        _devices = QueryDevices()
                    End If
                    Return _devices
                End SyncLock
            End Get
        End Property

        ''' <summary>Der eigene Speicher der Karte mit diesem Schluessel, in Mebibyte. 0 heisst
        ''' unbekannt - dann darf der Aufrufer NICHTS annehmen.
        '''
        ''' Der Schluessel von <see cref="GpuAccelerationService"/> traegt hinten den Steckplatz
        ''' ("10de:2d05:0000:02:00.0"), Vulkan kennt ihn nicht. Verglichen werden deshalb nur
        ''' Hersteller und Geraet. Zwei baugleiche Karten sind damit nicht unterscheidbar - sie
        ''' haben aber auch denselben Speicher.</summary>
        Public Shared Function DeviceLocalMiBFor(key As String) As Integer
            If String.IsNullOrWhiteSpace(key) Then Return 0
            Dim parts = key.Split(":"c)
            If parts.Length < 2 Then Return 0
            Dim wanted = parts(0) & ":" & parts(1)
            For Each device In Devices
                If String.Equals(device.Key, wanted, StringComparison.OrdinalIgnoreCase) Then Return device.DeviceLocalMiB
            Next
            Return 0
        End Function

        Private Shared Function LibraryCandidates() As String()
            If OperatingSystem.IsWindows() Then Return {"vulkan-1.dll"}
            Return {"libvulkan.so.1", "libvulkan.so"}
        End Function

        Private Shared Function QueryDevices() As List(Of VulkanDevice)
            Dim result As New List(Of VulkanDevice)()
            ' macOS hat kein natives Vulkan, und das ist dort keine Stoerung, sondern die Bauart:
            ' gerechnet wird ueber Metal. Ohne diesen Ausstieg stuende in jedem Mac-Log eine Zeile
            ' ueber eine fehlende Bibliothek, die dort gar nicht fehlen KANN - eine Meldung, die
            ' beim Suchen in die falsche Richtung zeigt.
            If OperatingSystem.IsMacOS() Then Return result

            Dim library As IntPtr = IntPtr.Zero
            For Each candidate In LibraryCandidates()
                If NativeLibrary.TryLoad(candidate, library) Then Exit For
            Next
            If library = IntPtr.Zero Then
                DiagnosticLogService.LogAlways("Grafik", "Vulkan nicht ladbar - Speichergroesse der Karte unbekannt")
                Return result
            End If

            Dim instance As IntPtr = IntPtr.Zero
            Dim createInfo As IntPtr = IntPtr.Zero
            Dim destroy As DestroyInstanceFn = Nothing
            Try
                Dim create = GetExport(Of CreateInstanceFn)(library, "vkCreateInstance")
                Dim enumerate = GetExport(Of EnumerateDevicesFn)(library, "vkEnumeratePhysicalDevices")
                Dim deviceProps = GetExport(Of DevicePropertiesFn)(library, "vkGetPhysicalDeviceProperties")
                Dim memoryProps = GetExport(Of MemoryPropertiesFn)(library, "vkGetPhysicalDeviceMemoryProperties")
                destroy = GetExport(Of DestroyInstanceFn)(library, "vkDestroyInstance")

                ' VkInstanceCreateInfo: nur der Typ wird gebraucht, alles andere bleibt null. Wir
                ' wollen weder Erweiterungen noch Schichten, nur die Geraeteliste.
                createInfo = Marshal.AllocHGlobal(64)
                For i = 0 To 63
                    Marshal.WriteByte(createInfo, i, 0)
                Next
                Marshal.WriteInt32(createInfo, 0, VkStructureTypeInstanceCreateInfo)

                If create(createInfo, IntPtr.Zero, instance) <> 0 OrElse instance = IntPtr.Zero Then
                    DiagnosticLogService.LogAlways("Grafik", "Vulkan-Instanz liess sich nicht anlegen")
                    Return result
                End If

                Dim count As UInteger = 0
                enumerate(instance, count, Nothing)
                If count = 0UI Then Return result
                ' NICHT "handles" nennen: Handles ist ein VB-Schluesselwort, und die Sprache
                ' unterscheidet keine Gross- und Kleinschreibung.
                Dim found(CInt(count) - 1) As IntPtr
                enumerate(instance, count, found)

                For Each handle In found
                    If handle = IntPtr.Zero Then Continue For
                    result.Add(ReadDevice(handle, deviceProps, memoryProps))
                Next

                DiagnosticLogService.LogAlways("Grafik",
                    "Vulkan meldet " & result.Count.ToString() & " Karte(n): " &
                    String.Join(", ", result.Select(Function(d) $"{d.Name} [{d.Key}] {d.DeviceLocalMiB} MiB")))
            Catch ex As Exception
                ' Eine Karte, deren Speicher unbekannt bleibt, ist kein Fehler - der Aufrufer
                ' rechnet dann wie bisher. Deshalb nur eine Zeile, kein Weiterreichen.
                DiagnosticLogService.LogAlways("Grafik", $"Vulkan-Abfrage gescheitert: {ex.GetType().Name}")
                result.Clear()
            Finally
                Try
                    ' Die Instanz muss weg, BEVOR die Bibliothek entladen wird - danach zeigt der
                    ' Zeiger ins Leere.
                    If instance <> IntPtr.Zero AndAlso destroy IsNot Nothing Then destroy(instance, IntPtr.Zero)
                Catch
                End Try
                If createInfo <> IntPtr.Zero Then Marshal.FreeHGlobal(createInfo)
                NativeLibrary.Free(library)
            End Try
            Return result
        End Function

        ''' <summary>Die beiden Bloecke werden als rohe Bytes gelesen und von Hand zerlegt. Ein
        ''' Structure mit passendem Aufbau waere schoener, aber VkPhysicalDeviceMemoryProperties
        ''' enthaelt zwei feste Felder mit 32 bzw. 16 Eintraegen - in VB waeren das zwei eigene
        ''' Typen samt Ausrichtungsfragen, und falsch ausgerichtet liest man stillschweigend Unsinn.
        ''' Ueber die Versaetze steht die Rechnung dagegen im Klartext da.</summary>
        Private Shared Function ReadDevice(handle As IntPtr,
                                           deviceProps As DevicePropertiesFn,
                                           memoryProps As MemoryPropertiesFn) As VulkanDevice
            ' VkPhysicalDeviceProperties: apiVersion(4) driverVersion(4) vendorID(4) deviceID(4)
            ' deviceType(4) deviceName(256) ...
            Dim props(1023) As Byte
            deviceProps(handle, props)
            Dim vendorId = BitConverter.ToUInt32(props, 8)
            Dim deviceId = BitConverter.ToUInt32(props, 12)
            Dim name = Text.Encoding.ASCII.GetString(props, 20, 256).TrimEnd(ChrW(0)).Trim()

            ' VkPhysicalDeviceMemoryProperties: memoryTypeCount(4) memoryTypes(32 * 8)
            ' memoryHeapCount(4) memoryHeaps(16 * 16). Ein VkMemoryHeap ist size(8) + flags(4),
            ' auf 8 ausgerichtet, also 16 Byte je Eintrag.
            Dim memory(1023) As Byte
            memoryProps(handle, memory)
            Const heapsOffset As Integer = 4 + 32 * 8 + 4
            Dim heapCount = CInt(Math.Min(16UI, BitConverter.ToUInt32(memory, 4 + 32 * 8)))
            Dim largest As ULong = 0UL
            For i = 0 To heapCount - 1
                Dim size = BitConverter.ToUInt64(memory, heapsOffset + i * 16)
                Dim flags = CULng(BitConverter.ToUInt32(memory, heapsOffset + i * 16 + 8))
                If (flags And HeapDeviceLocal) <> 0UL AndAlso size > largest Then largest = size
            Next

            Return New VulkanDevice With {
                .Key = $"{vendorId:x4}:{deviceId:x4}",
                .Name = name,
                .DeviceLocalMiB = CInt(Math.Min(Integer.MaxValue, CLng(largest \ (1024UL * 1024UL))))
            }
        End Function

        Private Shared Function GetExport(Of T)(library As IntPtr, name As String) As T
            Return Marshal.GetDelegateForFunctionPointer(Of T)(NativeLibrary.GetExport(library, name))
        End Function

    End Class

End Namespace
