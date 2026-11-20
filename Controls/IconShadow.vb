Imports Avalonia
Imports Avalonia.Media
Imports Avalonia.Rendering
Imports Avalonia.Rendering.SceneGraph
Imports Avalonia.Skia
Imports SkiaSharp

Namespace Controls

    ''' <summary>Welcher vorgerechnete Schatten unter einem <see cref="ShadowedSvgIcon"/> liegt.</summary>
    Public Enum IconShadowKind
        ''' <summary>Kein Schatten.</summary>
        None
        ''' <summary>Nur der enge, dunkle Rand ohne Versatz.</summary>
        Rim
        ''' <summary>Der Rand und darunter der weiche Hof um Symbol samt Rand, leicht nach unten
        ''' versetzt.</summary>
        RimAndHalo
    End Enum

    ''' <summary>Ein Symbol mit VORGERECHNETEM Schatten, fuer die Zeichen und Knoepfe auf den
    ''' Galeriekacheln.
    '''
    ''' Vorher trugen Sterne, Herz und Metadatenzeichen dort je zwei DropShadowEffect (einen am
    ''' Symbol, einen am Knopf oder Kaestchen darum). Jeder Effekt ist beim Zeichnen eine eigene
    ''' Zwischenflaeche samt Weichzeichnen, je Kachel und je Bild neu; bei vollem Raster weit ueber
    ''' hundert, und auf schwaecheren Grafikkarten ruckelte das Rollen. Jetzt rechnet
    ''' <see cref="IconShadowCache"/> den Schatten je Symbol, Groesse und Art EINMAL als Bild, und
    ''' alle Kacheln zeichnen dasselbe Bild vor ihrem Symbol.
    '''
    ''' Der Schatten liegt im selben Element wie das Symbol: Deckkraft und Sichtbarkeit (immer,
    ''' beim Ueberfahren, versteckt in der Fotowand) gelten deshalb ohne eigene Regel fuer beide.
    ''' Fuer Stile zaehlt das Element als <see cref="SvgIcon"/> (StyleKeyOverride), Selektoren wie
    ''' <c>icons|SvgIcon.rating-star</c> greifen also unveraendert.</summary>
    Public Class ShadowedSvgIcon
        Inherits SvgIcon
        Implements ICustomHitTest

        Public Shared ReadOnly ShadowProperty As StyledProperty(Of IconShadowKind) =
            AvaloniaProperty.Register(Of ShadowedSvgIcon, IconShadowKind)(NameOf(Shadow), IconShadowKind.RimAndHalo)

        ''' <summary>Schatten nur, solange das Symbol HELL ist. Fuer Symbole in der Kontrastfarbe
        ''' der Akzentfarbe (FP.OnAccent): die ist ab Werk weiss, bei einer hellen Akzentfarbe aber
        ''' dunkel, und ein schwarzer Rand machte ein dunkles Symbol nur fetter. Die Farbe wird bei
        ''' jedem Zeichnen gelesen; ein Wechsel der Akzentfarbe setzt einen neuen Pinsel und
        ''' zeichnet damit neu (IconBrush wirkt aufs Zeichnen).</summary>
        Public Shared ReadOnly ShadowOnlyWhenLightProperty As StyledProperty(Of Boolean) =
            AvaloniaProperty.Register(Of ShadowedSvgIcon, Boolean)(NameOf(ShadowOnlyWhenLight), False)

        Shared Sub New()
            AffectsRender(Of ShadowedSvgIcon)(ShadowProperty, ShadowOnlyWhenLightProperty)
        End Sub

        Public Property Shadow As IconShadowKind
            Get
                Return GetValue(ShadowProperty)
            End Get
            Set(value As IconShadowKind)
                SetValue(ShadowProperty, value)
            End Set
        End Property

        Public Property ShadowOnlyWhenLight As Boolean
            Get
                Return GetValue(ShadowOnlyWhenLightProperty)
            End Get
            Set(value As Boolean)
                SetValue(ShadowOnlyWhenLightProperty, value)
            End Set
        End Property

        Protected Overrides ReadOnly Property StyleKeyOverride As Type
            Get
                Return GetType(SvgIcon)
            End Get
        End Property

        ''' <summary>Ob der Schatten gerade gezeichnet wird (siehe <see cref="ShadowOnlyWhenLight"/>).</summary>
        Public ReadOnly Property IsShadowDrawn As Boolean
            Get
                If Shadow = IconShadowKind.None Then Return False
                If Not ShadowOnlyWhenLight Then Return True
                Dim solid = TryCast(IconBrush, ISolidColorBrush)
                If solid Is Nothing Then Return True
                Return IsLight(solid.Color)
            End Get
        End Property

        ''' <summary>Hell heisst: gewichtete Helligkeit der sRGB-Kanaele ueber 140 von 255. Die
        ''' Kontrastfarbe der Akzentfarbe ist entweder Weiss oder fast Schwarz
        ''' (SettingsViewModel.OnAccentColor); die Schwelle trennt beide sicher.</summary>
        Public Shared Function IsLight(color As Color) As Boolean
            Return 0.2126 * color.R + 0.7152 * color.G + 0.0722 * color.B > 140
        End Function

        Public Overrides Sub Render(context As DrawingContext)
            If Bounds.Width > 0 AndAlso Bounds.Height > 0 AndAlso IsShadowDrawn Then
                Dim image = IconShadowCache.GetShadow(Source, Bounds.Size, Shadow)
                If image IsNot Nothing Then context.Custom(New ShadowDrawOperation(image))
            End If
            MyBase.Render(context)
        End Sub

        ''' <summary>Getroffen wird das Rechteck des Symbols, nicht der Schatten. Avalonia zaehlt
        ''' alles Gezeichnete als Trefferflaeche; ohne das ragte der Schatten ueber den Knopf
        ''' hinaus, und der Stern rechts daneben (im Baum weiter oben) naehme dem linken die
        ''' Klicks an dessen rechtem Rand weg.</summary>
        Public Function HitTest(point As Point) As Boolean Implements ICustomHitTest.HitTest
            Return New Rect(Bounds.Size).Contains(point)
        End Function

    End Class

    ''' <summary>Zeichnet ein Schattenbild direkt in Skia. Avalonias Bitmap kennt kein reines
    ''' Alphaformat (Avalonia.Skia setzt nur Rgb565, Bgra8888, Rgba8888 und Rgb32 um und wandelt
    ''' alles andere nach Bgra8888), der Schatten braucht aber nur Deckung: ein Alpha8-Bild ist ein
    ''' Viertel so gross, die Farbe (Schwarz) kommt beim Zeichnen aus dem Pinsel. Die Deckkraft
    ''' der Vorfahren liefert der Lease (CurrentOpacity; 1, wenn Avalonia sie als Zwischenebene
    ''' anlegt, sonst das Produkt, das Avalonia selbst an jeden Pinsel multipliziert).</summary>
    Friend NotInheritable Class ShadowDrawOperation
        Implements ICustomDrawOperation

        Private ReadOnly _image As IconShadowCache.ShadowImage

        Public Sub New(image As IconShadowCache.ShadowImage)
            _image = image
        End Sub

        Public ReadOnly Property Bounds As Rect Implements ICustomDrawOperation.Bounds
            Get
                Return _image.Placement
            End Get
        End Property

        Public Function HitTest(p As Point) As Boolean Implements ICustomDrawOperation.HitTest
            Return False
        End Function

        Public Overloads Function Equals(other As ICustomDrawOperation) As Boolean Implements IEquatable(Of ICustomDrawOperation).Equals
            Dim o = TryCast(other, ShadowDrawOperation)
            Return o IsNot Nothing AndAlso o._image Is _image
        End Function

        Public Sub Render(context As ImmediateDrawingContext) Implements ICustomDrawOperation.Render
            Dim feature = context.TryGetFeature(Of ISkiaSharpApiLeaseFeature)()
            If feature Is Nothing Then Return
            Using lease = feature.Lease()
                Dim alpha = CByte(Math.Round(255 * Math.Clamp(lease.CurrentOpacity, 0, 1)))
                If alpha = 0 Then Return
                Using paint = New SKPaint With {.Color = New SKColor(0, 0, 0, alpha)}
                    Dim p = _image.Placement
                    ' Verkleinert mit Mipmaps: die Kante bleibt weich statt stufig.
                    lease.SkCanvas.DrawImage(_image.Image, SKRect.Create(CSng(p.X), CSng(p.Y), CSng(p.Width), CSng(p.Height)),
                                             New SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear), paint)
                End Using
            End Using
        End Sub

        Public Sub Dispose() Implements IDisposable.Dispose
            ' Das Bild gehoert dem Zwischenspeicher, nicht der Zeichenoperation.
        End Sub

    End Class

    ''' <summary>Rechnet und teilt die Schattenbilder fuer <see cref="ShadowedSvgIcon"/>.
    '''
    ''' WIE GERECHNET WIRD: im Aufbau der DropShadowEffect, die der Schatten ersetzt. Avalonia macht
    ''' aus BlurRadius ein Sigma von 0,288675 * Radius + 0,5 (Avalonia.Skia,
    ''' DrawingContextImpl.SkBlurRadiusToSigma) und legt mit Skias DropShadow einen schwarzen,
    ''' weichgezeichneten Abdruck unter den Inhalt. Hier entsteht dasselbe in Skia: der Abdruck des
    ''' Symbols als Rand, darunter der Hof, der aus Symbol UND Rand entsteht, weil der alte Hof am
    ''' Knopf sass und beides einschloss.
    '''
    ''' EIN BILD JE SYMBOL, GROESSE UND ART, in fester Aufloesung (<see cref="PixelsPerUnit"/>
    ''' Bildpunkte je Einheit) statt je Bildschirmskalierung, als reine Deckung (Alpha8). Beim
    ''' Zeichnen wird es skaliert; der Schatten ist ueberall weich, Mipmaps verkleinern ihn ohne
    ''' Stufen.</summary>
    Public NotInheritable Class IconShadowCache

        Private Sub New()
        End Sub

        ' DIE STELLSCHRAUBEN DES SCHATTENS, alle an dieser einen Stelle. Als Effekte galten bis zur
        ' Umstellung: Rand Weichzeichnung 2, Schwarz, einfach; Hof Weichzeichnung 5, Versatz 1,
        ' Deckung C0. Der vorgerechnete Schatten ist bewusst etwas dunkler.

        ''' <summary>Bildpunkte je Einheit im gerechneten Bild: das Doppelte einer Anzeige mit
        ''' doppelter Skalierung. Das kleinste Sigma ist gut eine Einheit, mehr braucht es nicht.</summary>
        Public Const PixelsPerUnit As Integer = 4
        ''' <summary>Weichzeichnung des engen Rands, als BlurRadius wie bei DropShadowEffect.</summary>
        Public Const RimBlurRadius As Double = 2
        ''' <summary>Wie oft der Rand uebereinander liegt: 1 entspricht dem alten Effekt, 2 deckt
        ''' kraeftiger, ohne breiter zu werden.</summary>
        Public Const RimLayers As Integer = 2
        ''' <summary>Weichzeichnung des Hofs, als BlurRadius wie bei DropShadowEffect.</summary>
        Public Const HaloBlurRadius As Double = 5
        ''' <summary>Versatz des Hofs nach unten, in Einheiten.</summary>
        Public Const HaloOffsetY As Double = 1
        ''' <summary>Deckung des Hofs (als Effekt C0).</summary>
        Public Const HaloAlpha As Byte = &HE0

        ''' <summary>Sigma, das Avalonia aus einem BlurRadius macht (Avalonia.Skia,
        ''' DrawingContextImpl.SkBlurRadiusToSigma).</summary>
        Public Shared Function BlurRadiusToSigma(radius As Double) As Double
            If radius <= 0 Then Return 0
            Return 0.288675 * radius + 0.5
        End Function

        ''' <summary>Ein fertiges Schattenbild und wo es relativ zum Symbol liegt.</summary>
        Public NotInheritable Class ShadowImage
            ''' <summary>Reine Deckung (Alpha8), unveraenderlich, von allen Kacheln geteilt.</summary>
            Public Property Image As SKImage
            Public Property Placement As Rect
        End Class

        ' Nur auf dem Anzeigefaden benutzt (aus Render).
        Private Shared ReadOnly Images As New Dictionary(Of String, ShadowImage)(StringComparer.Ordinal)

        ''' <summary>Wie viele Schattenbilder gerechnet vorliegen.</summary>
        Public Shared ReadOnly Property Count As Integer
            Get
                Return Images.Count
            End Get
        End Property

        ''' <summary>Was alle Schattenbilder zusammen an Pixelspeicher belegen, in Byte.</summary>
        Public Shared ReadOnly Property TotalBytes As Long
            Get
                Dim sum As Long = 0
                For Each entry In Images.Values
                    If entry IsNot Nothing Then sum += CLng(entry.Image.Width) * entry.Image.Height
                Next
                Return sum
            End Get
        End Property

        ''' <summary>Das Schattenbild fuer ein Symbol in dieser Groesse, beim ersten Mal gerechnet,
        ''' danach geteilt. Nothing, wenn das Symbol nicht lesbar ist.</summary>
        Public Shared Function GetShadow(source As String, size As Size, kind As IconShadowKind) As ShadowImage
            If kind = IconShadowKind.None OrElse String.IsNullOrWhiteSpace(source) Then Return Nothing
            Dim inv = Globalization.CultureInfo.InvariantCulture
            Dim key = String.Join("|", SvgIcon.ResolveIconSource(source), size.Width.ToString("R", inv), size.Height.ToString("R", inv), kind.ToString())
            Dim cached As ShadowImage = Nothing
            If Images.TryGetValue(key, cached) Then Return cached
            ' MESSPUNKT: einmal je Symbol, Groesse und Art.
            Dim built = Services.PerformanceTraceService.Measure("Symbole: Schattenbild rechnen", Function() Build(source, size, kind))
            ' Auch ein Fehlschlag wird gemerkt, sonst rechnete jede Kachel ihn erneut.
            Images(key) = built
            Return built
        End Function

        Private Shared Function Build(source As String, size As Size, kind As IconShadowKind) As ShadowImage
            Dim outline = SvgIcon.GetOutline(source, size)
            If outline Is Nothing Then Return Nothing

            Dim rimSigma = CSng(BlurRadiusToSigma(RimBlurRadius) * PixelsPerUnit)
            Dim haloSigma = CSng(BlurRadiusToSigma(HaloBlurRadius) * PixelsPerUnit)
            ' Rand um das Symbol: drei Sigma des weitesten Weichzeichners plus Versatz, aufgerundet.
            Dim reach = If(kind = IconShadowKind.RimAndHalo,
                           3 * BlurRadiusToSigma(HaloBlurRadius) + HaloOffsetY,
                           3 * BlurRadiusToSigma(RimBlurRadius))
            Dim pad = Math.Ceiling(reach)
            Dim pixelWidth = CInt(Math.Ceiling((size.Width + 2 * pad) * PixelsPerUnit))
            Dim pixelHeight = CInt(Math.Ceiling((size.Height + 2 * pad) * PixelsPerUnit))
            Dim info = New SKImageInfo(pixelWidth, pixelHeight, SKColorType.Bgra8888, SKAlphaType.Premul)

            Using mask = New SKBitmap(info),
                  rim = New SKBitmap(info),
                  result = New SKBitmap(info)
                ' 1) Der Abdruck: das Symbol deckend in Schwarz, mit derselben Abbildung wie SvgIcon.Render.
                Using canvas = New SKCanvas(mask)
                    canvas.Clear(SKColors.Transparent)
                    canvas.Scale(PixelsPerUnit, PixelsPerUnit)
                    canvas.Translate(CSng(pad), CSng(pad))
                    Dim m = outline.Transform
                    canvas.Concat(New SKMatrix(CSng(m.M11), CSng(m.M21), CSng(m.M31),
                                               CSng(m.M12), CSng(m.M22), CSng(m.M32),
                                               0, 0, 1))
                    Using path = outline.BuildSkiaPath(),
                          paint = New SKPaint With {.IsAntialias = True, .Color = SKColors.Black}
                        If outline.IsStroked Then
                            paint.Style = SKPaintStyle.Stroke
                            paint.StrokeWidth = CSng(outline.StrokeWidth)
                            paint.StrokeCap = SKStrokeCap.Round
                            paint.StrokeJoin = SKStrokeJoin.Round
                        Else
                            paint.Style = SKPaintStyle.Fill
                        End If
                        canvas.DrawPath(path, paint)
                    End Using
                End Using

                ' 2) Der Rand: der weichgezeichnete Abdruck, so oft uebereinander wie RimLayers sagt.
                Using canvas = New SKCanvas(rim)
                    canvas.Clear(SKColors.Transparent)
                    Using filter = SKImageFilter.CreateDropShadowOnly(0, 0, rimSigma, rimSigma, SKColors.Black),
                          paint = New SKPaint With {.ImageFilter = filter}
                        For layer = 1 To Math.Max(1, RimLayers)
                            canvas.DrawBitmap(mask, 0, 0, paint)
                        Next
                    End Using
                End Using

                Using canvas = New SKCanvas(result)
                    canvas.Clear(SKColors.Transparent)
                    If kind = IconShadowKind.RimAndHalo Then
                        ' 3) Der Hof entsteht aus Symbol samt Rand, wie frueher am Knopf.
                        Using withRim = New SKBitmap(info)
                            Using rimCanvas = New SKCanvas(withRim)
                                rimCanvas.Clear(SKColors.Transparent)
                                rimCanvas.DrawBitmap(rim, 0, 0)
                                rimCanvas.DrawBitmap(mask, 0, 0)
                            End Using
                            Using filter = SKImageFilter.CreateDropShadowOnly(0, CSng(HaloOffsetY * PixelsPerUnit), haloSigma, haloSigma, New SKColor(0, 0, 0, HaloAlpha)),
                                  paint = New SKPaint With {.ImageFilter = filter}
                                canvas.DrawBitmap(withRim, 0, 0, paint)
                            End Using
                        End Using
                    End If
                    ' 4) Darueber der Rand.
                    canvas.DrawBitmap(rim, 0, 0)
                End Using

                ' Nur die Deckung behalten: Alpha8, ein Byte je Bildpunkt.
                Dim image As SKImage
                Using alphaOnly = New SKBitmap(New SKImageInfo(pixelWidth, pixelHeight, SKColorType.Alpha8, SKAlphaType.Premul))
                    If Not result.ExtractAlpha(alphaOnly) Then Return Nothing
                    Using pixels = alphaOnly.PeekPixels()
                        image = If(pixels Is Nothing, Nothing, SKImage.FromPixelCopy(pixels))
                    End Using
                End Using
                If image Is Nothing Then Return Nothing
                Return New ShadowImage With {
                    .Image = image,
                    .Placement = New Rect(-pad, -pad, pixelWidth / PixelsPerUnit, pixelHeight / PixelsPerUnit)
                }
            End Using
        End Function

    End Class

End Namespace
