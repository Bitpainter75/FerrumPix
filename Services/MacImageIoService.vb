Imports System
Imports System.IO
Imports System.Runtime.InteropServices

Namespace Services

    ''' <summary>
    ''' HEIC/HEIF/AVIF über die Bordmittel von macOS. ImageIO liest diese Formate dort seit jeher,
    ''' weil das Betriebssystem den HEVC-Dekoder selbst mitbringt - auf einem Mac ist HEIC das
    ''' Standardformat der Kamera, ein zusätzlich installiertes libheif wäre dort die Ausnahme.
    '''
    ''' Diese Datei ist bewusst die EINZIGE Stelle mit Apple-Interop. HeifDecodeService fragt nur
    ''' IsAvailable und bekommt sonst dieselben Rückgaben wie vom libheif-Weg; es kennt weder
    ''' CoreFoundation noch ImageIO und muss auch nicht wissen, auf welchem System es läuft.
    '''
    ''' Auf Linux und Windows wird nichts geladen: die beiden Handles bleiben leer, IsAvailable
    ''' meldet False, und keine der DllImport-Deklarationen unten wird je angefasst. Für diese
    ''' Systeme ändert die Datei damit nichts, der libheif-Weg bleibt dort der einzige.
    '''
    ''' An der Linie des Projekts ändert das nichts: ImageIO gehört zum Betriebssystem, FerrumPix
    ''' liefert weiterhin keinen HEVC-Dekoder aus (siehe Klassenkommentar von HeifDecodeService).
    ''' </summary>
    Public NotInheritable Class MacImageIoService

        Private Sub New()
        End Sub

        ''' <summary>Name für die Diagnose und für Feldberichte.</summary>
        Public Const DecoderName As String = "ImageIO"

        ''' <summary>True nur auf macOS und nur, wenn beide Frameworks geladen werden konnten.
        ''' Sonst False - der Aufrufer nimmt dann seinen anderen Weg.</summary>
        Public Shared ReadOnly Property IsAvailable As Boolean
            Get
                Return _coreFoundation <> IntPtr.Zero AndAlso _imageIo <> IntPtr.Zero
            End Get
        End Property

        ''' <summary>Die Datei als PNG-Strom, in voller Auflösung und bereits gedreht - dieselbe
        ''' Form, die HeifDecodeService.ExtractPreview auch vom libheif-Weg liefert.
        '''
        ''' Die Drehung aus dem Container legt ImageIO selbst auf
        ''' (kCGImageSourceCreateThumbnailWithTransform), genau wie libheif es mit seinen
        ''' Standard-Dekodieroptionen tut. Der Aufrufer darf also KEINE zweite Orientierungs-
        ''' korrektur draufsetzen.
        '''
        ''' "Thumbnail" ist hier nur der Name der Programmierschnittstelle: ohne den Schlüssel
        ''' kCGImageSourceThumbnailMaxPixelSize liefert ImageIO das Bild in voller Grösse, und
        ''' kCGImageSourceCreateThumbnailFromImageAlways sorgt dafür, dass es das Hauptbild
        ''' aufbereitet statt die kleine Vorschau aus dem Container zu nehmen.</summary>
        Public Shared Function TryExtractPng(path As String) As MemoryStream
            If Not IsAvailable OrElse String.IsNullOrWhiteSpace(path) Then Return Nothing

            Dim url As IntPtr = IntPtr.Zero
            Dim source As IntPtr = IntPtr.Zero
            Dim options As IntPtr = IntPtr.Zero
            Dim image As IntPtr = IntPtr.Zero
            Dim data As IntPtr = IntPtr.Zero
            Dim pngType As IntPtr = IntPtr.Zero
            Dim destination As IntPtr = IntPtr.Zero

            Try
                url = CreateFileUrl(path)
                If url = IntPtr.Zero Then Return Nothing
                source = CGImageSourceCreateWithURL(url, IntPtr.Zero)
                If source = IntPtr.Zero Then Return Nothing

                Dim fromImageAlways = ReadConstant(_imageIo, "kCGImageSourceCreateThumbnailFromImageAlways")
                Dim withTransform = ReadConstant(_imageIo, "kCGImageSourceCreateThumbnailWithTransform")
                Dim booleanTrue = ReadConstant(_coreFoundation, "kCFBooleanTrue")
                If fromImageAlways = IntPtr.Zero OrElse withTransform = IntPtr.Zero OrElse
                   booleanTrue = IntPtr.Zero Then Return Nothing

                Dim keys = {fromImageAlways, withTransform}
                Dim values = {booleanTrue, booleanTrue}
                options = CreateDictionary(keys, values)
                If options = IntPtr.Zero Then Return Nothing

                image = CGImageSourceCreateThumbnailAtIndex(source, PrimaryImageIndex(source), options)
                If image = IntPtr.Zero Then Return Nothing

                data = CFDataCreateMutable(IntPtr.Zero, UIntPtr.Zero)
                pngType = CreateUtf8String("public.png")
                If data = IntPtr.Zero OrElse pngType = IntPtr.Zero Then Return Nothing
                destination = CGImageDestinationCreateWithData(data, pngType, New UIntPtr(1UI), IntPtr.Zero)
                If destination = IntPtr.Zero Then Return Nothing

                CGImageDestinationAddImage(destination, image, IntPtr.Zero)
                If Not CGImageDestinationFinalize(destination) Then Return Nothing

                Dim length = CFDataGetLength(data)
                Dim bytesPointer = CFDataGetBytePtr(data)
                If length <= 0 OrElse length > Integer.MaxValue OrElse bytesPointer = IntPtr.Zero Then Return Nothing

                Dim bytes(CInt(length) - 1) As Byte
                Marshal.Copy(bytesPointer, bytes, 0, bytes.Length)
                Return New MemoryStream(bytes, writable:=False)
            Catch ex As Exception
                ' Ohne Log wäre auf einem fremden Mac nicht zu unterscheiden, ob die Datei defekt
                ' ist oder ein Symbol des Systems fehlt.
                DiagnosticLogService.LogException("MacImageIo.ExtractPng", ex)
                Return Nothing
            Finally
                Release(destination)
                Release(pngType)
                Release(data)
                Release(image)
                Release(options)
                Release(source)
                Release(url)
            End Try
        End Function

        ''' <summary>Maße ohne vollständiges Dekodieren - die Kopfdaten reichen dafür.
        ''' Bei den Orientierungen 5 bis 8 steht das Bild quer, dann sind Breite und Höhe zu
        ''' tauschen: TryExtractPng liefert die gedrehten Pixel, und beide Wege müssen dasselbe
        ''' Format melden.</summary>
        Public Shared Function TryGetSize(path As String) As (Width As Integer, Height As Integer)
            If Not IsAvailable OrElse String.IsNullOrWhiteSpace(path) Then Return (0, 0)

            Dim url As IntPtr = IntPtr.Zero
            Dim source As IntPtr = IntPtr.Zero
            Dim properties As IntPtr = IntPtr.Zero
            Try
                url = CreateFileUrl(path)
                If url = IntPtr.Zero Then Return (0, 0)
                source = CGImageSourceCreateWithURL(url, IntPtr.Zero)
                If source = IntPtr.Zero Then Return (0, 0)
                properties = CGImageSourceCopyPropertiesAtIndex(source, PrimaryImageIndex(source), IntPtr.Zero)
                If properties = IntPtr.Zero Then Return (0, 0)

                Dim width = ReadDictionaryInteger(properties, ReadConstant(_imageIo, "kCGImagePropertyPixelWidth"))
                Dim height = ReadDictionaryInteger(properties, ReadConstant(_imageIo, "kCGImagePropertyPixelHeight"))
                Dim orientation = ReadDictionaryInteger(properties, ReadConstant(_imageIo, "kCGImagePropertyOrientation"))
                If orientation >= 5 AndAlso orientation <= 8 Then
                    Dim swap = width
                    width = height
                    height = swap
                End If
                Return (width, height)
            Catch ex As Exception
                DiagnosticLogService.LogException("MacImageIo.GetSize", ex)
                Return (0, 0)
            Finally
                Release(properties)
                Release(source)
                Release(url)
            End Try
        End Function

        ' ── CoreFoundation-Handgriffe ────────────────────────────────────────────

        ''' <summary>Der Index des HAUPTBILDES in der Datei.
        '''
        ''' Nicht immer 0: eine HEIF-Datei kann mehrere Bilder tragen (Sequenzen, Serienbilder,
        ''' Tiefen- und Hilfsbilder), und welches davon das eigentliche Foto ist, steht im Container.
        ''' Fest den Index 0 zu nehmen liefert dort ein anderes Bild und andere Masse.
        '''
        ''' Die Funktion gibt es erst ab macOS 10.14. Auf einem aelteren System fehlt das Symbol, und
        ''' ein Aufruf risse mit einer Ausnahme ab - deshalb wird erst nachgesehen, ob es da ist, und
        ''' im Zweifel bleibt es bei 0, also genau bei dem Verhalten von vorher.
        '''
        ''' Der Rueckgabewert ist eine Zahl und kein CoreFoundation-Objekt: nichts freizugeben.</summary>
        Private Shared Function PrimaryImageIndex(source As IntPtr) As UIntPtr
            If source = IntPtr.Zero Then Return UIntPtr.Zero
            If ReadSymbolAddress(_imageIo, "CGImageSourceGetPrimaryImageIndex") = IntPtr.Zero Then
                Return UIntPtr.Zero
            End If
            Try
                Return CGImageSourceGetPrimaryImageIndex(source)
            Catch ex As Exception
                DiagnosticLogService.LogException("MacImageIo.PrimaryIndex", ex)
                Return UIntPtr.Zero
            End Try
        End Function

        Private Shared Function CreateFileUrl(path As String) As IntPtr
            Dim pathBytes = Text.Encoding.UTF8.GetBytes(path)
            Return CFURLCreateFromFileSystemRepresentation(
                IntPtr.Zero, pathBytes, New UIntPtr(CUInt(pathBytes.Length)), False)
        End Function

        Private Shared Function CreateUtf8String(value As String) As IntPtr
            ' Die Bytes selbst erzeugen statt die Zeichenkette marshallen zu lassen: so steht die
            ' Kodierung im Code und hängt nicht am CharSet-Standard der Laufzeit.
            Dim bytes = Text.Encoding.UTF8.GetBytes(value & vbNullChar)
            Return CFStringCreateWithCString(IntPtr.Zero, bytes, CFStringEncodingUtf8)
        End Function

        ''' <summary>Ein Optionswörterbuch mit den Standard-Rückrufen für CoreFoundation-Typen.
        ''' Die sind wichtig: ohne sie behielte das Wörterbuch weder Schlüssel noch Werte am Leben
        ''' und vergliche Schlüssel über die Zeigeradresse statt über CFEqual.</summary>
        Private Shared Function CreateDictionary(keys As IntPtr(), values As IntPtr()) As IntPtr
            ' kCFTypeDictionaryKeyCallBacks ist eine Struktur, keine Referenz - hier zählt die
            ' ADRESSE des Symbols, nicht der Inhalt an dieser Adresse.
            Dim keyCallbacks = ReadSymbolAddress(_coreFoundation, "kCFTypeDictionaryKeyCallBacks")
            Dim valueCallbacks = ReadSymbolAddress(_coreFoundation, "kCFTypeDictionaryValueCallBacks")
            If keyCallbacks = IntPtr.Zero OrElse valueCallbacks = IntPtr.Zero Then Return IntPtr.Zero
            Return CFDictionaryCreate(
                IntPtr.Zero, keys, values, New UIntPtr(CUInt(keys.Length)), keyCallbacks, valueCallbacks)
        End Function

        Private Shared Function ReadDictionaryInteger(dictionary As IntPtr, key As IntPtr) As Integer
            If dictionary = IntPtr.Zero OrElse key = IntPtr.Zero Then Return 0
            Dim number = CFDictionaryGetValue(dictionary, key)
            If number = IntPtr.Zero Then Return 0
            Dim value As Integer
            Return If(CFNumberGetValue(number, CFNumberSInt32Type, value), value, 0)
        End Function

        ''' <summary>Adresse eines exportierten Symbols.</summary>
        Private Shared Function ReadSymbolAddress(handle As IntPtr, name As String) As IntPtr
            If handle = IntPtr.Zero Then Return IntPtr.Zero
            Dim symbol As IntPtr
            If Not NativeLibrary.TryGetExport(handle, name, symbol) Then Return IntPtr.Zero
            Return symbol
        End Function

        ''' <summary>Wert einer exportierten Zeigerkonstante (etwa eines kCG...-Schlüssels).</summary>
        Private Shared Function ReadConstant(handle As IntPtr, name As String) As IntPtr
            Dim symbol = ReadSymbolAddress(handle, name)
            If symbol = IntPtr.Zero Then Return IntPtr.Zero
            Return Marshal.ReadIntPtr(symbol)
        End Function

        Private Shared Sub Release(value As IntPtr)
            If value <> IntPtr.Zero Then CFRelease(value)
        End Sub

        Private Shared Function LoadFrameworkIfAvailable(path As String) As IntPtr
            If Not OperatingSystem.IsMacOS() Then Return IntPtr.Zero
            Try
                Return NativeLibrary.Load(path)
            Catch
                ' Kein Log: auf einem System ohne diese Frameworks ist das der Normalfall, nicht
                ' ein Fehler. Der Aufrufer sieht es an IsAvailable.
                Return IntPtr.Zero
            End Try
        End Function

        ' ── Native Bindung ───────────────────────────────────────────────────────

        Private Const CoreFoundationFramework As String =
            "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation"
        Private Const ImageIoFramework As String =
            "/System/Library/Frameworks/ImageIO.framework/ImageIO"

        ' Die Handles dienen nur dazu, die exportierten Konstanten auszulesen - die Funktionen
        ' darunter laufen über DllImport. Beide Wege öffnen dieselbe Bibliothek, das ist harmlos
        ' (dlopen zählt mit), und die Handles bleiben absichtlich bis zum Programmende offen.
        Private Shared ReadOnly _coreFoundation As IntPtr =
            LoadFrameworkIfAvailable(CoreFoundationFramework)
        Private Shared ReadOnly _imageIo As IntPtr =
            LoadFrameworkIfAvailable(ImageIoFramework)

        ' kCFNumberSInt32Type = 3; kCFStringEncodingUTF8 = 0x08000100.
        Private Const CFNumberSInt32Type As Integer = 3
        Private Const CFStringEncodingUtf8 As UInteger = &H8000100UI

        <DllImport(CoreFoundationFramework, CallingConvention:=CallingConvention.Cdecl)>
        Private Shared Function CFURLCreateFromFileSystemRepresentation(
            allocator As IntPtr,
            buffer As Byte(),
            bufferLength As UIntPtr,
            <MarshalAs(UnmanagedType.I1)> isDirectory As Boolean) As IntPtr
        End Function

        <DllImport(CoreFoundationFramework, CallingConvention:=CallingConvention.Cdecl)>
        Private Shared Function CFDictionaryCreate(
            allocator As IntPtr,
            keys As IntPtr(),
            values As IntPtr(),
            count As UIntPtr,
            keyCallbacks As IntPtr,
            valueCallbacks As IntPtr) As IntPtr
        End Function

        <DllImport(CoreFoundationFramework, CallingConvention:=CallingConvention.Cdecl)>
        Private Shared Function CFDictionaryGetValue(dictionary As IntPtr, key As IntPtr) As IntPtr
        End Function

        ' Der Rueckgabewert ist in C ein Boolean von EINEM Byte. Ihn als Integer zu holen ist
        ' falsch: bei einer so schmalen Rueckgabe sind die oberen Registerbits laut ABI auf arm64
        ' wie auf x64 unbestimmt, ein Nein mit Muell darueber laese sich als Ja. Deshalb Boolean
        ' mit ausdruecklichem I1 - dann wertet die Laufzeit genau das eine Byte aus.
        <DllImport(CoreFoundationFramework, CallingConvention:=CallingConvention.Cdecl)>
        Private Shared Function CFNumberGetValue(
            number As IntPtr,
            numberType As Integer,
            ByRef value As Integer) As <MarshalAs(UnmanagedType.I1)> Boolean
        End Function

        <DllImport(CoreFoundationFramework, CallingConvention:=CallingConvention.Cdecl)>
        Private Shared Function CFDataCreateMutable(
            allocator As IntPtr,
            capacity As UIntPtr) As IntPtr
        End Function

        <DllImport(CoreFoundationFramework, CallingConvention:=CallingConvention.Cdecl)>
        Private Shared Function CFStringCreateWithCString(
            allocator As IntPtr,
            value As Byte(),
            encoding As UInteger) As IntPtr
        End Function

        <DllImport(CoreFoundationFramework, CallingConvention:=CallingConvention.Cdecl)>
        Private Shared Function CFDataGetLength(data As IntPtr) As Long
        End Function

        <DllImport(CoreFoundationFramework, CallingConvention:=CallingConvention.Cdecl)>
        Private Shared Function CFDataGetBytePtr(data As IntPtr) As IntPtr
        End Function

        <DllImport(CoreFoundationFramework, CallingConvention:=CallingConvention.Cdecl)>
        Private Shared Sub CFRelease(value As IntPtr)
        End Sub

        <DllImport(ImageIoFramework, CallingConvention:=CallingConvention.Cdecl)>
        Private Shared Function CGImageSourceCreateWithURL(url As IntPtr, options As IntPtr) As IntPtr
        End Function

        <DllImport(ImageIoFramework, CallingConvention:=CallingConvention.Cdecl)>
        Private Shared Function CGImageSourceCopyPropertiesAtIndex(
            source As IntPtr,
            index As UIntPtr,
            options As IntPtr) As IntPtr
        End Function

        ' size_t, also so breit wie ein Zeiger. Erst ab macOS 10.14 vorhanden - der Aufruf laeuft
        ' deshalb ueber PrimaryImageIndex, das vorher nach dem Symbol sieht.
        <DllImport(ImageIoFramework, CallingConvention:=CallingConvention.Cdecl)>
        Private Shared Function CGImageSourceGetPrimaryImageIndex(source As IntPtr) As UIntPtr
        End Function

        <DllImport(ImageIoFramework, CallingConvention:=CallingConvention.Cdecl)>
        Private Shared Function CGImageSourceCreateThumbnailAtIndex(
            source As IntPtr,
            index As UIntPtr,
            options As IntPtr) As IntPtr
        End Function

        <DllImport(ImageIoFramework, CallingConvention:=CallingConvention.Cdecl)>
        Private Shared Function CGImageDestinationCreateWithData(
            data As IntPtr,
            type As IntPtr,
            count As UIntPtr,
            options As IntPtr) As IntPtr
        End Function

        <DllImport(ImageIoFramework, CallingConvention:=CallingConvention.Cdecl)>
        Private Shared Sub CGImageDestinationAddImage(
            destination As IntPtr,
            image As IntPtr,
            properties As IntPtr)
        End Sub

        ' Wie bei CFNumberGetValue: in C ein Boolean von einem Byte, deshalb Boolean mit I1 und
        ' nicht Integer.
        <DllImport(ImageIoFramework, CallingConvention:=CallingConvention.Cdecl)>
        Private Shared Function CGImageDestinationFinalize(destination As IntPtr) As <MarshalAs(UnmanagedType.I1)> Boolean
        End Function

    End Class

End Namespace
