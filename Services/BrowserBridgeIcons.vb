Imports System
Imports System.Collections.Concurrent
Imports System.IO
Imports System.Net
Imports System.Threading.Tasks
Imports Avalonia.Platform
Imports SkiaSharp

Namespace Services

    ''' <summary>Das Symbol der Anwendung fuer den Tab des Browsers und den Startbildschirm eines
    ''' Telefons: /favicon.ico (das fragt ein Browser von selbst ab), /favicon.png in 32 Punkten und
    ''' /apple-touch-icon.png in 180 Punkten, dazu /icon-192.png. Gerechnet aus dem Programmsymbol
    ''' (Assets/FerrumPix_Icon.png, 512 Punkte) beim ersten Abruf und danach gehalten. Die beiden
    ''' grossen landen auf dem Startbildschirm und sind deshalb deckend quadratisch (BuildSquareIcon).
    '''
    ''' <para>OHNE SITZUNG ABRUFBAR, wie das Bild des Anmeldefensters: das Symbol zeigt der Browser
    ''' schon auf der Anmeldeseite, und es verraet nichts, was nicht in jedem Paket der Anwendung
    ''' liegt. Die Pruefung des Wegs (IsAcceptedRoute) gilt trotzdem.</para></summary>
    Partial Public NotInheritable Class BrowserBridge

        Friend Const IconIcoRoute As String = "/favicon.ico"
        Friend Const IconPngRoute As String = "/favicon.png"
        Friend Const TouchIconRoute As String = "/apple-touch-icon.png"
        Friend Const LargeIconRoute As String = "/icon-192.png"

        Private Const AppIconPngAsset As String = "avares://FerrumPix/Assets/FerrumPix_Icon.png"
        Private Const AppIconIcoAsset As String = "avares://FerrumPix/Assets/FerrumPix_Icon.ico"

        Private Shared ReadOnly IconCache As New ConcurrentDictionary(Of String, Byte())(StringComparer.Ordinal)

        ''' <summary>Gehoert der Pfad einem der Symbole?</summary>
        Friend Shared Function IsIconRoute(path As String) As Boolean
            Return path = IconIcoRoute OrElse path = IconPngRoute OrElse path = TouchIconRoute OrElse path = LargeIconRoute
        End Function

        ''' <summary>Das Symbol in der Groesse des Pfads, als PNG (bzw. ICO fuer /favicon.ico).</summary>
        Friend Shared Function IconBytes(path As String) As Byte()
            Return IconCache.GetOrAdd(path, AddressOf BuildIcon)
        End Function

        Private Shared Function BuildIcon(path As String) As Byte()
            If path = IconIcoRoute Then Return ReadAsset(AppIconIcoAsset)
            If path <> IconPngRoute Then Return BuildSquareIcon(If(path = TouchIconRoute, 180, 192), HomeScreenIconFraction)
            Using source = SKBitmap.Decode(ReadAsset(AppIconPngAsset))
                Using scaled = source.Resize(New SKImageInfo(32, 32, SKColorType.Rgba8888, SKAlphaType.Premul),
                                             New SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear))
                    Return PngEncoder.EncodeToBytes(scaled, PngPurpose.Export)
                End Using
            End Using
        End Function

        ''' <summary>Anteil der Kante, den der Inhalt des Symbols auf dem Startbildschirm einnimmt. Das
        ''' System rundet die Kachel selbst ab; der Rand haelt die eigenen Ecken des Symbols davon frei.</summary>
        Friend Const HomeScreenIconFraction As Single = 0.86F

        ''' <summary>Hintergrund der quadratischen Symbole: der des dunklen Erscheinungsbilds.</summary>
        Private Const SquareIconBackground As String = "#0B0E11"

        ''' <summary>Ein deckendes, quadratisches Symbol fuer den Startbildschirm. Das Programmsymbol hat
        ''' transparente Ecken, eine Linse, die ueber den Rahmen ragt, und oben einen leeren Streifen;
        ''' ein Telefon fuellt Transparenz mit eigener Farbe (schwarz oder weiss) und das Symbol saesse
        ''' schief in der Kachel. Deshalb: Hintergrund der Anwendung ueber die ganze Flaeche, darauf der
        ''' sichtbare Inhalt (nach Deckung begrenzt) mittig mit der laengeren Seite auf
        ''' contentFraction der Kante.</summary>
        Friend Shared Function BuildSquareIcon(size As Integer, contentFraction As Single) As Byte()
            Using source = SKBitmap.Decode(ReadAsset(AppIconPngAsset))
                Dim content = OpaqueBounds(source)
                Dim scale = size * contentFraction / Math.Max(content.Width, content.Height)
                Dim width = content.Width * scale
                Dim height = content.Height * scale
                Dim target = SKRect.Create((size - width) / 2.0F, (size - height) / 2.0F, width, height)
                Using bitmap As New SKBitmap(New SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Premul))
                    Using canvas As New SKCanvas(bitmap)
                        canvas.Clear(SKColor.Parse(SquareIconBackground))
                        Using image = SKImage.FromBitmap(source)
                            canvas.DrawImage(image, content, target, New SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear))
                        End Using
                    End Using
                    Return PngEncoder.EncodeToBytes(bitmap, PngPurpose.Export)
                End Using
            End Using
        End Function

        ''' <summary>Das Rechteck der Pixel mit nennenswerter Deckung; der weiche Schatten unter der Linse
        ''' zaehlt nicht mit. Ohne solche Pixel das ganze Bild.</summary>
        Private Shared Function OpaqueBounds(bitmap As SKBitmap) As SKRect
            Dim width = bitmap.Width
            Dim height = bitmap.Height
            Dim left = width, top = height, right = -1, bottom = -1
            For y = 0 To height - 1
                For x = 0 To width - 1
                    If bitmap.GetPixel(x, y).Alpha >= 32 Then
                        If x < left Then left = x
                        If x > right Then right = x
                        If y < top Then top = y
                        If y > bottom Then bottom = y
                    End If
                Next
            Next
            If right < 0 Then Return SKRect.Create(0, 0, width, height)
            Return New SKRect(left, top, right + 1, bottom + 1)
        End Function

        Private Shared Function ReadAsset(asset As String) As Byte()
            Using stream = AssetLoader.Open(New Uri(asset)), copy As New MemoryStream()
                stream.CopyTo(copy)
                Return copy.ToArray()
            End Using
        End Function

        Private Shared Async Function ServeIconAsync(context As HttpListenerContext, path As String) As Task
            Dim bytes = IconBytes(path)
            Dim response = context.Response
            response.StatusCode = 200
            response.ContentType = If(path = IconIcoRoute, "image/x-icon", "image/png")
            response.Headers("Cache-Control") = "public, max-age=86400"
            response.Headers("X-Content-Type-Options") = "nosniff"
            response.ContentLength64 = bytes.Length
            Await response.OutputStream.WriteAsync(bytes)
            response.Close()
        End Function
    End Class
End Namespace
