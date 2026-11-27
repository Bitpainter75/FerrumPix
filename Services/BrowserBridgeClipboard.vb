Imports System
Imports System.Collections.Generic
Imports System.IO
Imports System.Linq
Imports System.Net
Imports System.Reflection
Imports System.Runtime.ExceptionServices
Imports System.Security.Cryptography
Imports System.Text
Imports System.Text.Json
Imports System.Threading
Imports System.Threading.Tasks
Imports Avalonia
Imports Avalonia.Input
Imports Avalonia.Input.Platform
Imports Avalonia.Media.Imaging
Imports Avalonia.Threading
Imports SkiaSharp

Namespace Services

    ' ------------------------------------------------------------------------------------
    ' Die Zwischenablage zwischen dem Geraet des Browsers und der Anwendung.
    '
    ' DIE ANWENDUNG HAT IM BROWSER EINE EIGENE ZWISCHENABLAGE. Die Plattform ohne Bildschirm
    ' haelt sie nur im Speicher (HeadlessClipboardImplStub, mit ilspycmd nachgesehen); ins System
    ' des Servers wird nichts geschrieben, im Behaelter gibt es dort auch keines. Daran aendert
    ' sich nichts. Die Bruecke legt nur einen Stellvertreter davor (BridgeClipboard), der jedes
    ' Ablegen der Anwendung bemerkt, und setzt selbst ein, was vom Geraet kommt.
    '
    ' AUS DER ANWENDUNG ZUM GERAET: legt die Anwendung Text oder ein Bild ab, bekommt der
    ' verbundene Browser (und nur er) eine kurze Nachricht {"t":"clip"} mit einer Nummer. Den
    ' Inhalt holt die Seite ueber GET /clipboard mit der Kennung ihrer Seite; ein grosses Bild
    ' haelt so den WebSocket nicht auf, und die Seite holt nur, was sie schreiben kann. Kopierte
    ' Dateien der Galerie gehen nicht hinaus (ihr Text waeren Pfade auf dem Server), eigene
    ' Ablagen wie kopierte Ebenen und Anpassungen gehen ohnehin nicht ueber die Zwischenablage.
    '
    ' VOM GERAET IN DIE ANWENDUNG: die Seite faengt STRG+V ab, liest das Ereignis paste des
    ' Browsers und schickt Text oder ein PNG mit POST /clipboard; erst nach der Antwort reicht sie
    ' die Taste an die Anwendung weiter, deren Einfuegen dann den neuen Inhalt sieht. Was von hier
    ' kommt, geht nicht wieder an die Seite zurueck.
    '
    ' NUR DER BEDIENENDE BROWSER. Abholen und Einsetzen verlangen neben Sitzung und Recht auf die
    ' volle Oberflaeche die Kennung der Seite, die gerade am WebSocket haengt; ein abgeloester
    ' Browser oder eine zweite Seite derselben Sitzung kommt nicht heran. Inhalte stehen nie im
    ' Protokoll.
    ' ------------------------------------------------------------------------------------
    Partial Public NotInheritable Class BrowserBridge

        Private Const ClipboardRoute As String = "/clipboard"

        ''' <summary>Hoechstgroesse eines Texts in der Zwischenablage, in beiden Richtungen: 16 MB
        ''' (UTF-8). Das ganze Diagnoseprotokoll passt, und mehr haelt kein Textfeld sinnvoll.</summary>
        Friend Const MaxClipboardTextBytes As Integer = 16 * 1024 * 1024

        ''' <summary>Hoechstgroesse eines Bilds in der Zwischenablage, in beiden Richtungen: 64 MB
        ''' als PNG. Ein ganzes Foto mit 24 Megapixeln liegt darunter. Anders als beim Hochladen geht
        ''' der Inhalt in den Speicher, nicht auf die Platte; deshalb die engere Grenze.</summary>
        Friend Const MaxClipboardImageBytes As Integer = 64 * 1024 * 1024

        ''' <summary>So viele Bildpunkte darf ein eingefuegtes Bild haben. Ein PNG von wenigen
        ''' Kilobytes kann sich sonst zu Gigabytes entpacken.</summary>
        Private Const MaxClipboardImagePixels As Long = 200000000L

        ''' <summary>Dieselbe Form wie die eigenen PNG-Bytes der Anwendung
        ''' (ClipboardPathService.PngBytesFormat): gleich ist ein Format ueber Art und Namen.</summary>
        Private Shared ReadOnly ClipboardPngFormat As DataFormat(Of Byte()) = DataFormat.CreateBytesPlatformFormat("image/png")

        ''' <summary>Die Pfadliste, die die Anwendung beim Kopieren von Dateien ablegt
        ''' (ClipboardPathService.InternalPathsFormat).</summary>
        Private Const InternalPathsIdentifier As String = "FerrumPixInternalPaths"

        Private Shared ReadOnly ClipboardLock As New Object()
        Private Shared _clipboardProxy As BridgeClipboard
        Private Shared _originalClipboard As IClipboard
        Private Shared _clipboardSerial As Long
        Private Shared _outgoingClip As OutgoingClip

        ''' <summary>Die Kennung der Seite, die gerade am WebSocket haengt.</summary>
        Private Shared _clientToken As String = ""

        ''' <summary>Was die Anwendung zuletzt abgelegt hat, so weit es zum Geraet darf.</summary>
        Private NotInheritable Class OutgoingClip
            Public Id As Long
            Public Text As String
            ''' <summary>Das Bild als PNG. Eine Aufgabe, weil ein Bitmap erst kodiert werden muss;
            ''' das geschieht im Hintergrund, und das Abholen wartet darauf.</summary>
            Public Image As Task(Of Byte())
            Public Width As Integer
            Public Height As Integer
            ''' <summary>Ein Teil blieb weg, weil er die Grenze ueberschreitet.</summary>
            Public TooLarge As Boolean
        End Class

        ''' <summary>Der Stellvertreter vor der Zwischenablage der Plattform. Er reicht jeden Aufruf
        ''' durch und meldet danach jedes Ablegen der Anwendung.
        '''
        ''' <para>Die Schnittstelle traegt NotClientImplementable; die Klasse entsteht deshalb wie der
        ''' Zeichentakt der Bruecke erst zur Laufzeit (<see cref="DispatchProxy"/>). Eingesetzt wird
        ''' sie im Verzeichnis des Toolkits, aus dem das Fenster der Plattform seine Zwischenablage
        ''' bei jeder Anfrage holt (HeadlessWindowImpl.TryGetFeature).</para></summary>
        Friend Class BridgeClipboard
            Inherits DispatchProxy

            Public Inner As IClipboard

            Protected Overrides Function Invoke(targetMethod As MethodInfo, args As Object()) As Object
                Dim result As Object = Nothing
                Try
                    result = targetMethod.Invoke(Inner, args)
                Catch ex As TargetInvocationException When ex.InnerException IsNot Nothing
                    ExceptionDispatchInfo.Capture(ex.InnerException).Throw()
                End Try
                If targetMethod.Name = "SetDataAsync" AndAlso args IsNot Nothing AndAlso args.Length = 1 Then
                    AppClipboardChanged(TryCast(args(0), IAsyncDataTransfer))
                End If
                Return result
            End Function
        End Class

        ''' <summary>Setzt den Stellvertreter vor die Zwischenablage der Plattform. Das Verzeichnis
        ''' des Toolkits ist beim Bauen nicht sichtbar (PrivateApi), deshalb ueber Reflexion; fehlt
        ''' es in einer neuen Fassung, bleibt die Zwischenablage, wie sie ist, nur ohne Weg zum
        ''' Geraet.</summary>
        Private Shared Sub InstallClipboard()
            If _clipboardProxy IsNot Nothing Then Return
            Try
                Dim locator = GetType(AvaloniaLocator).GetProperty("CurrentMutable", BindingFlags.Public Or BindingFlags.Static)?.GetValue(Nothing)
                Dim getService = locator?.GetType().GetMethod("GetService", New Type() {GetType(Type)})
                Dim bindToSelf = locator?.GetType().GetMethod("BindToSelf")
                Dim current = TryCast(getService?.Invoke(locator, New Object() {GetType(IClipboard)}), IClipboard)
                If current Is Nothing OrElse bindToSelf Is Nothing Then
                    DiagnosticLogService.LogAlways("BrowserBridge.Clipboard", "Zwischenablage der Plattform nicht erreichbar, kein Weg zum Geraet")
                    Return
                End If
                If TypeOf current Is BridgeClipboard Then Return
                Dim proxy = DirectCast(DirectCast(DispatchProxy.Create(Of IClipboard, BridgeClipboard)(), Object), BridgeClipboard)
                proxy.Inner = current
                bindToSelf.MakeGenericMethod(GetType(IClipboard)).Invoke(locator, New Object() {proxy})
                _originalClipboard = current
                _clipboardProxy = proxy
            Catch ex As Exception When TypeOf ex Is TargetInvocationException OrElse TypeOf ex Is ArgumentException OrElse
                                       TypeOf ex Is InvalidOperationException OrElse TypeOf ex Is MemberAccessException
                DiagnosticLogService.LogException("BrowserBridge.Clipboard", If(ex.InnerException, ex))
            End Try
        End Sub

        ''' <summary>Gibt dem Toolkit seine Zwischenablage zurueck (Anhalten im Pruefstand). Ihr
        ''' Inhalt bleibt: der Stellvertreter hatte keinen eigenen.</summary>
        Private Shared Sub RestoreClipboard()
            Dim original = _originalClipboard
            _clipboardProxy = Nothing
            _originalClipboard = Nothing
            _clientToken = ""
            SyncLock ClipboardLock
                _outgoingClip = Nothing
            End SyncLock
            If original Is Nothing Then Return
            Try
                Dim locator = GetType(AvaloniaLocator).GetProperty("CurrentMutable", BindingFlags.Public Or BindingFlags.Static)?.GetValue(Nothing)
                Dim bindToSelf = locator?.GetType().GetMethod("BindToSelf")
                bindToSelf?.MakeGenericMethod(GetType(IClipboard)).Invoke(locator, New Object() {original})
            Catch ex As Exception When TypeOf ex Is TargetInvocationException OrElse TypeOf ex Is ArgumentException OrElse
                                       TypeOf ex Is InvalidOperationException OrElse TypeOf ex Is MemberAccessException
                DiagnosticLogService.LogException("BrowserBridge.Clipboard", If(ex.InnerException, ex))
            End Try
        End Sub

        ''' <summary>Die Anwendung hat etwas abgelegt. Ohne verbundenen Browser bleibt es dabei; mit
        ''' einem bekommt er eine Nachricht, sobald der Inhalt gelesen ist.</summary>
        Private Shared Sub AppClipboardChanged(transfer As IAsyncDataTransfer)
            If transfer Is Nothing OrElse CurrentClient Is Nothing Then Return
            Dim id = Interlocked.Increment(_clipboardSerial)
            ' Gelesen wird auf dem Anzeigefaden, wo die Anwendung ihre Daten abgelegt hat, aber erst
            ' nach ihrem Aufruf: das Ablegen selbst wartet nicht auf die Bruecke.
            Dispatcher.UIThread.Post(Sub()
                                         Dim ignored = PublishClipboardAsync(id, transfer)
                                     End Sub)
        End Sub

        Private Shared Async Function PublishClipboardAsync(id As Long, transfer As IAsyncDataTransfer) As Task
            Try
                Dim clip = Await ReadAppClipboardAsync(id, transfer)
                ' Ist inzwischen etwas Neueres abgelegt, gilt das.
                If Interlocked.Read(_clipboardSerial) <> id Then Return
                SyncLock ClipboardLock
                    _outgoingClip = clip
                End SyncLock
                Dim message As New Dictionary(Of String, Object) From {
                    {"t", "clip"}, {"id", id}, {"text", clip.Text IsNot Nothing}, {"image", clip.Image IsNot Nothing},
                    {"w", clip.Width}, {"h", clip.Height}, {"large", clip.TooLarge}}
                Await SendTextAsync(JsonSerializer.Serialize(message))
            Catch ex As Exception
                DiagnosticLogService.LogException("BrowserBridge.Clipboard", ex)
            End Try
        End Function

        ''' <summary>Was von einem Ablegen der Anwendung zum Geraet darf: das Bild als PNG (eigene
        ''' PNG-Bytes unveraendert, ein Bitmap im Hintergrund kodiert) und der Text, ausser die
        ''' Anwendung hat Dateien kopiert. Dann waere der Text eine Liste von Pfaden auf dem Server,
        ''' mit denen das Geraet nichts anfangen kann.</summary>
        Private Shared Async Function ReadAppClipboardAsync(id As Long, transfer As IAsyncDataTransfer) As Task(Of OutgoingClip)
            Dim clip As New OutgoingClip With {.Id = id}
            Dim formats = transfer.Formats
            Dim carriesFiles = formats.Any(Function(f) f.Equals(DataFormat.File) OrElse
                                                       (f.Kind = DataFormatKind.Application AndAlso f.Identifier = InternalPathsIdentifier))
            If transfer.Contains(ClipboardPngFormat) Then
                Dim bytes = Await transfer.TryGetValueAsync(ClipboardPngFormat)
                If bytes IsNot Nothing AndAlso bytes.Length > 0 Then
                    If bytes.Length > MaxClipboardImageBytes Then
                        clip.TooLarge = True
                    Else
                        ReadPngSize(bytes, clip.Width, clip.Height)
                        clip.Image = Task.FromResult(bytes)
                    End If
                End If
            ElseIf transfer.Contains(DataFormat.Bitmap) Then
                ' Nicht freigeben: das Bitmap gehoert der Zwischenablage.
                Dim bitmap = Await transfer.TryGetValueAsync(DataFormat.Bitmap)
                If bitmap IsNot Nothing Then
                    clip.Width = bitmap.PixelSize.Width
                    clip.Height = bitmap.PixelSize.Height
                    clip.Image = Task.Run(Function() EncodeClipboardPng(bitmap))
                End If
            End If
            If Not carriesFiles AndAlso transfer.Contains(DataFormat.Text) Then
                Dim text = Await transfer.TryGetTextAsync()
                If Not String.IsNullOrEmpty(text) Then
                    If Encoding.UTF8.GetByteCount(text) > MaxClipboardTextBytes Then
                        clip.TooLarge = True
                    Else
                        clip.Text = text
                    End If
                End If
            End If
            Return clip
        End Function

        Private Shared Function EncodeClipboardPng(bitmap As Bitmap) As Byte()
            Using stream As New MemoryStream()
                bitmap.Save(stream, PngBitmapEncoderOptions.Default)
                Return stream.ToArray()
            End Using
        End Function

        ''' <summary>Breite und Hoehe aus dem Kopf eines PNG (IHDR), ohne zu dekodieren. Die Seite
        ''' erkennt daran ein Bild wieder, das sie selbst ins Geraet geschrieben hat.</summary>
        Private Shared Sub ReadPngSize(bytes As Byte(), ByRef width As Integer, ByRef height As Integer)
            If bytes.Length < 24 OrElse bytes(0) <> &H89 OrElse bytes(1) <> &H50 OrElse bytes(12) <> &H49 OrElse bytes(13) <> &H48 Then Return
            width = (CInt(bytes(16)) << 24) Or (CInt(bytes(17)) << 16) Or (CInt(bytes(18)) << 8) Or CInt(bytes(19))
            height = (CInt(bytes(20)) << 24) Or (CInt(bytes(21)) << 16) Or (CInt(bytes(22)) << 8) Or CInt(bytes(23))
        End Sub

        ''' <summary>Ob die Kennung die der Seite ist, die gerade am WebSocket haengt. Nur sie bedient
        ''' die Anwendung, und nur ihr gehoert die Zwischenablage.</summary>
        Private Shared Function IsServingClientToken(token As String) As Boolean
            If CurrentClient Is Nothing OrElse String.IsNullOrEmpty(token) Then Return False
            Dim serving = _clientToken
            If String.IsNullOrEmpty(serving) Then Return False
            Return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(token), Encoding.ASCII.GetBytes(serving))
        End Function

        ''' <summary>GET holt den zuletzt gemeldeten Inhalt ab, POST setzt einen vom Geraet ein.</summary>
        Private Shared Async Function HandleClipboardAsync(context As HttpListenerContext) As Task
            context.Response.KeepAlive = False
            ' Sitzung oder, fuer Werkzeuge, Basic; dazu das Recht auf die volle Oberflaeche, wie beim
            ' Hochladen. Nie WWW-Authenticate.
            If Not IsSignedIn(context) Then
                context.Response.StatusCode = 401
                context.Response.Close()
                Return
            End If
            If Not IsSignedInForDesktop(context) Then
                context.Response.StatusCode = 403
                context.Response.Close()
                Return
            End If
            If String.Equals(context.Request.HttpMethod, "GET", StringComparison.OrdinalIgnoreCase) Then
                Await ServeClipboardAsync(context)
            ElseIf String.Equals(context.Request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase) Then
                Await ReceiveClipboardAsync(context)
            Else
                context.Response.StatusCode = 405
                context.Response.Close()
            End If
        End Function

        ''' <summary>Liefert Text oder Bild des zuletzt gemeldeten Ablegens an die bedienende Seite.
        ''' Die Kennung steht wie beim Herunterladen in der Adresse; ein Browser schickt bei einem
        ''' GET der eigenen Seite keinen Kopf Origin mit, dafuer Sec-Fetch-Site, und wo der steht,
        ''' muss er die eigene Seite nennen.</summary>
        Private Shared Async Function ServeClipboardAsync(context As HttpListenerContext) As Task
            Dim request = context.Request
            Dim site = request.Headers("Sec-Fetch-Site")
            If (Not String.IsNullOrEmpty(site) AndAlso Not String.Equals(site, "same-origin", StringComparison.OrdinalIgnoreCase)) OrElse
               Not IsServingClientToken(request.QueryString("token")) Then
                context.Response.StatusCode = 403
                context.Response.Close()
                Return
            End If
            Dim id As Long
            Dim clip As OutgoingClip
            SyncLock ClipboardLock
                clip = _outgoingClip
            End SyncLock
            If clip Is Nothing OrElse Not Long.TryParse(request.QueryString("id"), Globalization.NumberStyles.None,
                                                        Globalization.CultureInfo.InvariantCulture, id) OrElse id <> clip.Id Then
                ' Inzwischen ist etwas anderes abgelegt, oder es gab nie etwas.
                context.Response.StatusCode = 410
                context.Response.Close()
                Return
            End If

            Dim body As Byte()
            Dim contentType As String
            Select Case request.QueryString("kind")
                Case "text"
                    If clip.Text Is Nothing Then
                        context.Response.StatusCode = 404
                        context.Response.Close()
                        Return
                    End If
                    body = Encoding.UTF8.GetBytes(clip.Text)
                    contentType = "text/plain; charset=utf-8"
                Case "image"
                    If clip.Image Is Nothing Then
                        context.Response.StatusCode = 404
                        context.Response.Close()
                        Return
                    End If
                    Try
                        body = Await clip.Image
                    Catch ex As Exception
                        ' Das Bitmap ging waehrend des Kodierens schon wieder weg.
                        DiagnosticLogService.LogException("BrowserBridge.Clipboard", ex)
                        context.Response.StatusCode = 410
                        context.Response.Close()
                        Return
                    End Try
                    If body Is Nothing OrElse body.Length > MaxClipboardImageBytes Then
                        context.Response.StatusCode = 413
                        context.Response.Close()
                        Return
                    End If
                    contentType = "image/png"
                Case Else
                    context.Response.StatusCode = 400
                    context.Response.Close()
                    Return
            End Select
            Try
                context.Response.StatusCode = 200
                context.Response.ContentType = contentType
                context.Response.Headers("Cache-Control") = "no-store"
                context.Response.ContentLength64 = body.Length
                Await context.Response.OutputStream.WriteAsync(body)
                context.Response.Close()
            Catch ex As Exception When TypeOf ex Is HttpListenerException OrElse TypeOf ex Is IOException OrElse
                                       TypeOf ex Is ObjectDisposedException
                ' Die Seite hat die Anfrage aufgegeben.
            End Try
        End Function

        ''' <summary>Setzt Text oder ein PNG vom Geraet in die Zwischenablage der Anwendung. Die
        ''' Reihenfolge der Pruefungen wie beim Hochladen: alles, was ohne den Inhalt entschieden
        ''' werden kann, kommt vor dem ersten gelesenen Byte. Geantwortet wird erst, wenn der
        ''' Inhalt in der Ablage liegt; danach schickt die Seite STRG+V.</summary>
        Private Shared Async Function ReceiveClipboardAsync(context As HttpListenerContext) As Task
            Dim request = context.Request
            If Not IsOwnOrigin(request) OrElse Not IsServingClientToken(request.Headers("X-FerrumPix-Token")) Then
                Await WriteResultAsync(context, 403, Nothing, LocalizationService.T("Die Seite ist veraltet. Bitte neu laden."))
                Return
            End If
            Dim mediaType = If(request.ContentType, "").Split(";"c)(0).Trim()
            Dim isText = String.Equals(mediaType, "text/plain", StringComparison.OrdinalIgnoreCase)
            Dim isImage = String.Equals(mediaType, "image/png", StringComparison.OrdinalIgnoreCase)
            If Not isText AndAlso Not isImage Then
                Await WriteResultAsync(context, 415, Nothing, LocalizationService.T("Einfügen aus der Zwischenablage ist fehlgeschlagen"))
                Return
            End If
            Dim length = request.ContentLength64
            If length < 0 Then
                context.Response.StatusCode = 411
                context.Response.Close()
                Return
            End If
            If length = 0 Then
                Await WriteResultAsync(context, 400, Nothing, LocalizationService.T("Einfügen aus der Zwischenablage ist fehlgeschlagen"))
                Return
            End If
            If length > If(isText, MaxClipboardTextBytes, MaxClipboardImageBytes) Then
                Await WriteResultAsync(context, 413, Nothing, LocalizationService.T("Zu groß für die Zwischenablage"))
                Return
            End If

            Dim body(CInt(length) - 1) As Byte
            Dim filled = 0
            Try
                While filled < body.Length
                    Dim read = Await request.InputStream.ReadAsync(body, filled, body.Length - filled)
                    If read <= 0 Then Exit While
                    filled += read
                End While
            Catch ex As Exception When TypeOf ex Is HttpListenerException OrElse TypeOf ex Is IOException OrElse
                                       TypeOf ex Is ObjectDisposedException
                filled = -1
            End Try
            If filled <> body.Length Then
                Await WriteResultAsync(context, 400, Nothing, LocalizationService.T("Die Übertragung wurde unterbrochen"))
                Return
            End If

            Dim item As New DataTransferItem()
            If isText Then
                item.SetText(Encoding.UTF8.GetString(body))
            Else
                ' Nur ein PNG, das sich lesen laesst und nicht zu gross entpackt. Abgelegt werden die
                ' Bytes in derselben Form wie die eigenen PNG-Bytes der Anwendung: der Editor liest
                ' sie bei jedem Einfuegen frisch (ClipboardPathService.TryReadOwnPngBitmapAsync),
                ' ein abgelegtes Bitmap gaebe er nach dem ersten Einfuegen frei.
                If Not IsAcceptablePng(body) Then
                    Await WriteResultAsync(context, 415, Nothing, LocalizationService.T("Einfügen aus der Zwischenablage ist fehlgeschlagen"))
                    Return
                End If
                item.Set(ClipboardPngFormat, body)
            End If
            Dim transfer As New DataTransfer()
            transfer.Add(item)

            ' Am Stellvertreter vorbei: was vom Geraet kommt, geht nicht wieder dorthin zurueck.
            Dim target = If(_originalClipboard, TryCast(_clipboardProxy, IClipboard))
            If target Is Nothing Then
                Await WriteResultAsync(context, 503, Nothing, LocalizationService.T("Einfügen aus der Zwischenablage ist fehlgeschlagen"))
                Return
            End If
            Dim place As Func(Of Task) = Function() target.SetDataAsync(transfer)
            Await Dispatcher.UIThread.InvokeAsync(place)
            Await WriteResultAsync(context, 200, Nothing, Nothing)
        End Function

        ''' <summary>Ein PNG, das sich lesen laesst, mit hoechstens <see cref="MaxClipboardImagePixels"/>
        ''' Bildpunkten. Geprueft wird nur der Kopf.</summary>
        Friend Shared Function IsAcceptablePng(bytes As Byte()) As Boolean
            Try
                Using data = SKData.CreateCopy(bytes), codec = SKCodec.Create(data)
                    If codec Is Nothing OrElse codec.EncodedFormat <> SKEncodedImageFormat.Png Then Return False
                    Dim pixels = CLng(codec.Info.Width) * codec.Info.Height
                    Return pixels > 0 AndAlso pixels <= MaxClipboardImagePixels
                End Using
            Catch ex As Exception When TypeOf ex Is ArgumentException OrElse TypeOf ex Is InvalidOperationException
                Return False
            End Try
        End Function

        ''' <summary>Die Texte der Seite fuer die Zwischenablage, dazu die Grenzen, damit die Seite
        ''' zu Grosses gar nicht erst schickt.</summary>
        Private Shared Sub AddClipboardPageTexts(texts As Dictionary(Of String, String))
            texts("clipReady") = LocalizationService.T("In FerrumPix kopiert")
            texts("clipCopy") = LocalizationService.T("In die Zwischenablage kopieren")
            texts("clipFailed") = LocalizationService.T("Die Zwischenablage dieses Geräts hat den Inhalt nicht angenommen")
            texts("clipImageInsecure") = LocalizationService.T("Bilder kommen nur über HTTPS in die Zwischenablage dieses Geräts")
            texts("clipTooLarge") = LocalizationService.T("Zu groß für die Zwischenablage")
            texts("pasteFailed") = LocalizationService.T("Einfügen aus der Zwischenablage ist fehlgeschlagen")
            texts("clipTextBytes") = MaxClipboardTextBytes.ToString(Globalization.CultureInfo.InvariantCulture)
            texts("clipImageBytes") = MaxClipboardImageBytes.ToString(Globalization.CultureInfo.InvariantCulture)
        End Sub

    End Class

End Namespace
