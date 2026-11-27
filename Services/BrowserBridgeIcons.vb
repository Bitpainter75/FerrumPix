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
    ''' (Assets/FerrumPix_Icon.png, 512 Punkte) beim ersten Abruf und danach gehalten.
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
            Dim size = If(path = IconPngRoute, 32, If(path = TouchIconRoute, 180, 192))
            Using source = SKBitmap.Decode(ReadAsset(AppIconPngAsset))
                Using scaled = source.Resize(New SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Premul),
                                             New SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear))
                    Return PngEncoder.EncodeToBytes(scaled, PngPurpose.Export)
                End Using
            End Using
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
