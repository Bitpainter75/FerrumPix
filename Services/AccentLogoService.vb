Imports System
Imports System.Collections.Generic
Imports System.IO
Imports Avalonia.Media.Imaging
Imports Avalonia.Platform
Imports SkiaSharp

Namespace Services

    ''' <summary>Das Logo der Fensterleiste in der Akzentfarbe: "Pix" ist im PNG orange eingebacken
    ''' und wird hier auf den Farbton des eingestellten Akzents gedreht.
    '''
    ''' Gedreht wird, nicht ersetzt: Farbton, Saettigung und Helligkeit jedes Bildpunkts werden um
    ''' den Abstand zwischen der Werksfarbe und dem Akzent verschoben. Verlauf, Glanzkante und Schein
    ''' des Schriftzugs bleiben damit erhalten, nur ihre Farbe folgt. Bei der Werksfarbe kommt das
    ''' Logo unveraendert heraus, bei Akzentstaerke 0 wird "Pix" grau.</summary>
    Public NotInheritable Class AccentLogoService

        Private Sub New()
        End Sub

        Public Const DarkLogoUri As String = "avares://FerrumPix/Assets/FerrumPix_TopBarDark_small.png"
        Public Const LightLogoUri As String = "avares://FerrumPix/Assets/FerrumPix_TopBarLight_small.png"

        ''' <summary>Die Farbe, in der "Pix" gezeichnet ist - die Werksfarbe des Akzents.</summary>
        Private Const LogoAccent As String = "#F08A1A"

        ''' <summary>Wo "Ferrum" aufhoert und "Pix" anfaengt, als Anteil der Bildbreite. Gemessen an
        ''' beiden Logos: "m" endet bei 68 bis 69 Prozent, "P" beginnt bei 70. Die Grenze ist
        ''' noetig, weil "Ferrum" im dunklen Logo einen warmen Schein traegt, der dieselben
        ''' Farbtoene hat wie das Orange und sonst mitgedreht wuerde.</summary>
        Private Const PixStartFraction As Double = 0.692

        Private Shared ReadOnly _cache As New Dictionary(Of String, Bitmap)(StringComparer.Ordinal)
        Private Shared ReadOnly _cacheLock As New Object()

        ''' <summary>Das Logo in der Akzentfarbe. Faellt bei jedem Fehler auf das unveraenderte Logo
        ''' zurueck; Nothing nur, wenn nicht einmal das zu laden ist.</summary>
        Public Shared Function GetTintedLogo(assetUri As String, accentColor As String) As Bitmap
            Dim key = assetUri & "|" & If(accentColor, "").ToUpperInvariant()
            SyncLock _cacheLock
                Dim cached As Bitmap = Nothing
                If _cache.TryGetValue(key, cached) Then Return cached
            End SyncLock
            Dim created As Bitmap = Nothing
            Try
                created = CreateTintedLogo(assetUri, accentColor)
            Catch ex As Exception
                DiagnosticLogService.LogException("AccentLogoService.GetTintedLogo", ex)
            End Try
            If created Is Nothing Then
                Try
                    Using stream = AssetLoader.Open(New Uri(assetUri))
                        created = New Bitmap(stream)
                    End Using
                Catch ex As Exception
                    DiagnosticLogService.LogException("AccentLogoService.LoadPlainLogo", ex)
                    Return Nothing
                End Try
            End If
            SyncLock _cacheLock
                _cache(key) = created
            End SyncLock
            Return created
        End Function

        Private Shared Function CreateTintedLogo(assetUri As String, accentColor As String) As Bitmap
            Dim accent As SKColor
            If Not SKColor.TryParse(If(accentColor, ""), accent) Then Return Nothing
            Dim reference = SKColor.Parse(LogoAccent)

            Using stream = AssetLoader.Open(New Uri(assetUri))
                Using bitmap = SKBitmap.Decode(stream)
                    If bitmap Is Nothing Then Return Nothing
                    TintPixels(bitmap, reference, accent)
                    Using image = SKImage.FromBitmap(bitmap)
                        Using data = image.Encode(SKEncodedImageFormat.Png, 100)
                            Using png = New MemoryStream(data.ToArray())
                                Return New Bitmap(png)
                            End Using
                        End Using
                    End Using
                End Using
            End Using
        End Function

        ''' <summary>Dreht die warmen Bildpunkte rechts der Grenze vom Bezug auf den Akzent. Friend
        ''' fuer den Pruefstand.</summary>
        Friend Shared Sub TintPixels(bitmap As SKBitmap, reference As SKColor, accent As SKColor)
            Dim refH, refS, refL As Double
            ToHsl(reference, refH, refS, refL)
            Dim accH, accS, accL As Double
            ToHsl(accent, accH, accS, accL)
            Dim hueShift = accH - refH
            Dim saturationScale = If(refS > 0, accS / refS, 1.0)

            ' SKColor ist nicht vormultipliziert; Pixels liest und schreibt in dieser Form.
            Dim pixels = bitmap.Pixels
            Dim width = bitmap.Width
            Dim startX = CInt(Math.Floor(width * PixStartFraction))
            For y = 0 To bitmap.Height - 1
                Dim row = y * width
                For x = startX To width - 1
                    Dim c = pixels(row + x)
                    If c.Alpha = 0 Then Continue For
                    Dim h, s, l As Double
                    ToHsl(c, h, s, l)
                    If Not IsWarm(h, s) Then Continue For
                    Dim newH = h + hueShift
                    Dim newS = Math.Min(1.0, s * saturationScale)
                    ' Die Helligkeit wandert mit, ohne den Verlauf flach zu druecken: dunkler als der
                    ' Bezug wird anteilig abgesenkt, heller anteilig zum Weiss hin angehoben.
                    Dim newL = If(accL <= refL,
                                  l * accL / Math.Max(0.0001, refL),
                                  l + (1.0 - l) * (accL - refL) / Math.Max(0.0001, 1.0 - refL))
                    pixels(row + x) = FromHsl(newH, newS, newL, c.Alpha)
                Next
            Next
            bitmap.Pixels = pixels
        End Sub

        ''' <summary>Orange im weiten Sinn: Rot bis Gelb, mit erkennbarer Farbe. "Ferrum" liegt bei
        ''' 200 bis 240 Grad und faellt schon am Farbton heraus.</summary>
        Private Shared Function IsWarm(hue As Double, saturation As Double) As Boolean
            Return saturation >= 0.25 AndAlso (hue <= 75.0 OrElse hue >= 345.0)
        End Function

        Private Shared Sub ToHsl(c As SKColor, ByRef h As Double, ByRef s As Double, ByRef l As Double)
            Dim r = c.Red / 255.0, g = c.Green / 255.0, b = c.Blue / 255.0
            Dim max = Math.Max(r, Math.Max(g, b))
            Dim min = Math.Min(r, Math.Min(g, b))
            l = (max + min) / 2.0
            Dim d = max - min
            If d < 0.000001 Then
                h = 0 : s = 0
                Return
            End If
            s = If(l > 0.5, d / (2.0 - max - min), d / (max + min))
            If max = r Then
                h = (g - b) / d + If(g < b, 6.0, 0.0)
            ElseIf max = g Then
                h = (b - r) / d + 2.0
            Else
                h = (r - g) / d + 4.0
            End If
            h *= 60.0
        End Sub

        Private Shared Function FromHsl(h As Double, s As Double, l As Double, alpha As Byte) As SKColor
            h = ((h Mod 360.0) + 360.0) Mod 360.0
            s = Math.Max(0.0, Math.Min(1.0, s))
            l = Math.Max(0.0, Math.Min(1.0, l))
            If s <= 0 Then
                Dim v = ToByte(l)
                Return New SKColor(v, v, v, alpha)
            End If
            Dim q = If(l < 0.5, l * (1.0 + s), l + s - l * s)
            Dim p = 2.0 * l - q
            Dim hk = h / 360.0
            Return New SKColor(ToByte(HueToChannel(p, q, hk + 1.0 / 3.0)),
                               ToByte(HueToChannel(p, q, hk)),
                               ToByte(HueToChannel(p, q, hk - 1.0 / 3.0)),
                               alpha)
        End Function

        Private Shared Function HueToChannel(p As Double, q As Double, t As Double) As Double
            If t < 0 Then t += 1.0
            If t > 1 Then t -= 1.0
            If t < 1.0 / 6.0 Then Return p + (q - p) * 6.0 * t
            If t < 0.5 Then Return q
            If t < 2.0 / 3.0 Then Return p + (q - p) * (2.0 / 3.0 - t) * 6.0
            Return p
        End Function

        Private Shared Function ToByte(value As Double) As Byte
            Return CByte(Math.Max(0, Math.Min(255, Math.Round(value * 255.0))))
        End Function

    End Class

End Namespace
