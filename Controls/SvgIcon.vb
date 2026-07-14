Imports System.Collections.Concurrent
Imports System.Globalization
Imports System.IO
Imports System.Text.RegularExpressions
Imports System.Threading
Imports System.Threading.Tasks
Imports Avalonia
Imports Avalonia.Controls
Imports Avalonia.Media
Imports Avalonia.Platform
Imports Avalonia.Threading

Namespace Controls

    Public Class SvgIcon
        Inherits Control

        Public Shared ReadOnly SourceProperty As StyledProperty(Of String) =
            AvaloniaProperty.Register(Of SvgIcon, String)(NameOf(Source))

        Public Shared ReadOnly IconBrushProperty As StyledProperty(Of IBrush) =
            AvaloniaProperty.Register(Of SvgIcon, IBrush)(NameOf(IconBrush), New SolidColorBrush(Color.Parse("#D6DCE1")))

        ' Nebenläufig, weil PreloadOutlineIconsAsync im Hintergrund einträgt, während Render auf dem
        ' UI-Faden liest. Doppelt geparste Icons sind dabei unkritisch - die Daten sind unveränderlich.
        Private Shared ReadOnly Cache As New ConcurrentDictionary(Of String, SvgIconData)()
        Private Shared _preloadStarted As Integer = 0
        Private Const IconAssetPrefix As String = "/Assets/Icons/"
        Private Const OutlineAssetBase As String = "avares://FerrumPix/Assets/Icons/outline/"
        Private Shared ReadOnly LegacyOutlineFileMap As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase) From {
            {"01_Home.svg", "home.svg"},
            {"02_Zurueck.svg", "arrow-left.svg"},
            {"03_Vorwaerts.svg", "arrow-right.svg"},
            {"04_Nach_oben.svg", "arrow-up.svg"},
            {"05_Nach_unten.svg", "arrow-down.svg"},
            {"07_Pinnnadel.svg", "pin.svg"},
            {"09_Speichern.svg", "device-floppy.svg"},
            {"11_Hinzufuegen.svg", "plus.svg"},
            {"14_Aktualisieren.svg", "refresh.svg"},
            {"18_Raster.svg", "layout-grid.svg"},
            {"19_Liste.svg", "layout-list.svg"},
            {"21_Vollbild.svg", "maximize.svg"},
            {"26_Anpassen.svg", "zoom-reset.svg"},
            {"34_Einstellungen.svg", "settings.svg"},
            {"14_Speichern_unter.svg", "file-export.svg"},
            {"28_Wiederherstellen.svg", "restore.svg"},
            {"01_Allgemein.svg", "settings.svg"},
            {"03_Aussehen.svg", "palette.svg"},
            {"09_Audio.svg", "volume.svg"},
            {"10_Vorschau.svg", "photo.svg"},
            {"11_Wiedergabe.svg", "player-play.svg"},
            {"33_Leistung.svg", "gauge.svg"},
            {"05_Ordner.svg", "folder.svg"},
            {"19_Filter.svg", "filter.svg"},
            {"21_Suchen.svg", "search.svg"},
            {"22_Sortieren.svg", "sort-ascending.svg"},
            {"01_Zuschneiden.svg", "crop.svg"},
            {"02_Groesse_aendern.svg", "resize.svg"},
            {"03_Gerade_richten.svg", "rotate-2.svg"},
            {"05_Kurven.svg", "chart-histogram.svg"},
            {"07_Belichtung.svg", "exposure.svg"},
            {"17_Farbmischer.svg", "color-filter.svg"},
            {"18_Color_Grading.svg", "adjustments-horizontal.svg"},
            {"24_Klarheit.svg", "sparkles.svg"},
            {"26_Retusche.svg", "wand.svg"},
            {"34_Text.svg", "text-size.svg"},
            {"35_Rahmen.svg", "frame.svg"},
            {"37_QR_Code.svg", "qrcode.svg"},
            {"39_Pinsel.svg", "brush.svg"},
            {"01_Info.svg", "circle-letter-i.svg"},
            {"02_EXIF.svg", "camera.svg"},
            {"03_IPTC.svg", "file-description.svg"},
            {"04_XMP.svg", "file-code.svg"},
            {"16_Diashow_starten.svg", "slideshow.svg"},
            {"26_Seitenverhaeltnis.svg", "aspect-ratio.svg"},
            {"29_Nach_links_drehen.svg", "rotate.svg"},
            {"30_Nach_rechts_drehen.svg", "rotate-clockwise.svg"},
            {"31_Horizontal_spiegeln.svg", "flip-horizontal.svg"},
            {"32_Vertikal_spiegeln.svg", "flip-vertical.svg"},
            {"35_Loeschen.svg", "trash.svg"},
            {"02_Herz.svg", "heart.svg"},
            {"11_Auge.svg", "eye.svg"},
            {"15_Ausgewaehlt.svg", "check.svg"},
            {"16_Nicht_ausgewaehlt.svg", "circle.svg"},
            {"18_Entfernen.svg", "x.svg"},
            {"23_Pfeil_nach_unten.svg", "arrow-down.svg"},
            {"27_Extern.svg", "external-link.svg"},
            {"32_Filter.svg", "filter.svg"},
            {"005_Rechteck.svg", "rectangle.svg"},
            {"003_Quadrat.svg", "square.svg"},
            {"007_Dreieck.svg", "triangle.svg"},
            {"011_Raute.svg", "square-rotated.svg"},
            {"013_Trapez.svg", "trapezoid.svg"},
            {"017_Oval.svg", "oval.svg"},
            {"018_Halbkreis.svg", "cone.svg"},
            {"031_Diamant_facette.svg", "diamond.svg"},
            {"032_Wuerfel.svg", "cube.svg"},
            {"047_Herz.svg", "heart.svg"},
            {"048_Sprechblase.svg", "speech-bubble-shape.svg"},
            {"051_Tropfen.svg", "droplet-shape.svg"},
            {"053_Spirale.svg", "spiral.svg"},
            {"061_Stern.svg", "star.svg"},
            {"077_Bild.svg", "photo.svg"},
            {"079_Play.svg", "player-play.svg"},
            {"080_Pause.svg", "player-pause.svg"},
            {"107_Check.svg", "check.svg"},
            {"108_X.svg", "x.svg"},
            {"141_Einfügen.svg", "clipboard-plus.svg"},
            {"200_Pipette.svg", "color-picker.svg"},
            {"31_Video.svg", "video.svg"}
        }

        Shared Sub New()
            AffectsRender(Of SvgIcon)(SourceProperty, IconBrushProperty)
        End Sub

        Public Property Source As String
            Get
                Return GetValue(SourceProperty)
            End Get
            Set(value As String)
                SetValue(SourceProperty, value)
            End Set
        End Property

        Public Property IconBrush As IBrush
            Get
                Return GetValue(IconBrushProperty)
            End Get
            Set(value As IBrush)
                SetValue(IconBrushProperty, value)
            End Set
        End Property

        Public Overrides Sub Render(context As DrawingContext)
            MyBase.Render(context)

            If Bounds.Width <= 0 OrElse Bounds.Height <= 0 OrElse String.IsNullOrWhiteSpace(Source) Then Return

            Dim icon = GetIcon(Source)
            If icon Is Nothing Then Return

            Dim fit = GetUniformFit(icon.ViewBox.Size, Bounds.Size)
            If fit.Width <= 0 OrElse fit.Height <= 0 Then Return

            Using state = context.PushTransform(Matrix.CreateTranslation(-icon.ViewBox.X, -icon.ViewBox.Y) *
                                                icon.SvgTransform *
                                                Matrix.CreateScale(fit.Scale, fit.Scale) *
                                                Matrix.CreateTranslation((Bounds.Width - fit.Width) / 2, (Bounds.Height - fit.Height) / 2))
                ' Kontur-Icons (fill="none" + stroke) werden nur mit Pen gezeichnet, damit sie als
                ' Umriss statt als flächig gefülltes Symbol dargestellt werden.
                If icon.IsStroked Then
                    Dim pen = New Pen(IconBrush, icon.StrokeWidth, lineCap:=PenLineCap.Round, lineJoin:=PenLineJoin.Round)
                    context.DrawGeometry(Nothing, pen, icon.Geometry)
                Else
                    context.DrawGeometry(IconBrush, Nothing, icon.Geometry)
                End If
            End Using
        End Sub

        Public Shared Function ResolveIconSource(source As String) As String
            If String.IsNullOrWhiteSpace(source) Then Return source
            If source.IndexOf("/Assets/Icons/outline/", StringComparison.OrdinalIgnoreCase) >= 0 Then Return source

            Dim iconIndex = source.IndexOf(IconAssetPrefix, StringComparison.OrdinalIgnoreCase)
            If iconIndex < 0 Then Return source

            Dim relativePath = source.Substring(iconIndex + IconAssetPrefix.Length)
            Dim fileName = Path.GetFileName(relativePath)
            Dim mappedFile = Nothing
            If Not LegacyOutlineFileMap.TryGetValue(fileName, mappedFile) Then Return source

            Return OutlineAssetBase & mappedFile
        End Function

        ''' <summary>Liest und zerlegt alle Kontur-Icons einmalig im Hintergrund und baut ihre
        ''' Geometrien anschließend häppchenweise auf dem UI-Faden. Ohne das wird jedes Symbol erst
        ''' geparst, wenn es zum ersten Mal gezeichnet wird - beim Aufklappen von "Formen und Symbole"
        ''' ruckelt dadurch das Scrollen, weil dort mehrere tausend Icons liegen.
        '''
        ''' Das Zerlegen (Datei lesen, reguläre Ausdrücke) läuft auf einem Arbeitsfaden, das Erzeugen der
        ''' Geometry-Objekte MUSS auf dem UI-Faden laufen: Geometry erbt von AvaloniaObject und ist an
        ''' den Faden gebunden, der es angelegt hat.
        ''' Läuft je Programmlauf genau einmal; weitere Aufrufe kehren sofort zurück.</summary>
        Public Shared Function PreloadOutlineIconsAsync() As Task
            If Interlocked.Exchange(_preloadStarted, 1) <> 0 Then Return Task.CompletedTask

            Return Task.Run(Sub()
                                Dim parsed As New List(Of KeyValuePair(Of String, SvgIconSource))()
                                Try
                                    For Each uri In AssetLoader.GetAssets(New Uri(OutlineAssetBase), Nothing)
                                        Dim source = uri.ToString()
                                        If Not source.EndsWith(".svg", StringComparison.OrdinalIgnoreCase) Then Continue For
                                        If Cache.ContainsKey(source) Then Continue For
                                        Dim parsedSource = LoadSource(source)
                                        If parsedSource IsNot Nothing Then parsed.Add(New KeyValuePair(Of String, SvgIconSource)(source, parsedSource))
                                    Next
                                Catch
                                End Try

                                ' In Häppchen, damit die Oberfläche zwischendurch zeichnen kann.
                                Const batchSize As Integer = 64
                                For start = 0 To parsed.Count - 1 Step batchSize
                                    Dim first = start
                                    Dispatcher.UIThread.Post(
                                        Sub()
                                            For i = first To Math.Min(first + batchSize - 1, parsed.Count - 1)
                                                Dim entry = parsed(i)
                                                If Cache.ContainsKey(entry.Key) Then Continue For
                                                Dim icon = BuildIcon(entry.Value)
                                                If icon IsNot Nothing Then Cache(entry.Key) = icon
                                            Next
                                        End Sub, DispatcherPriority.Background)
                                Next
                            End Sub)
        End Function

        Private Shared Function GetIcon(source As String) As SvgIconData
            Dim resolvedSource = ResolveIconSource(source)
            Dim cached As SvgIconData = Nothing
            If Cache.TryGetValue(resolvedSource, cached) Then Return cached

            Dim parsedSource = LoadSource(resolvedSource)
            If parsedSource Is Nothing Then Return Nothing

            Dim icon = BuildIcon(parsedSource)
            If icon IsNot Nothing Then Cache(resolvedSource) = icon
            Return icon
        End Function

        ''' Nur Lesen und Zerlegen - erzeugt keine Avalonia-Objekte und darf daher auf jedem Faden laufen.
        Private Shared Function LoadSource(resolvedSource As String) As SvgIconSource
            Try
                Using stream = AssetLoader.Open(New Uri(resolvedSource))
                    Using reader = New StreamReader(stream)
                        Return ParseSvg(reader.ReadToEnd())
                    End Using
                End Using
            Catch
                Return Nothing
            End Try
        End Function

        ''' Baut die Geometrie aus den Pfaddaten - nur auf dem UI-Faden aufrufen.
        Private Shared Function BuildIcon(source As SvgIconSource) As SvgIconData
            Dim geometry = BuildGeometry(source.PathData)
            If geometry Is Nothing Then Return Nothing

            Return New SvgIconData With {
                .ViewBox = source.ViewBox,
                .SvgTransform = source.SvgTransform,
                .Geometry = geometry,
                .IsStroked = source.IsStroked,
                .StrokeWidth = source.StrokeWidth
            }
        End Function

        Private Shared Function BuildGeometry(pathData As List(Of String)) As Geometry
            Dim group As New GeometryGroup()
            For Each d In pathData
                Try
                    group.Children.Add(Geometry.Parse(d))
                Catch
                End Try
            Next

            If group.Children.Count = 0 Then Return Nothing
            If group.Children.Count = 1 Then Return group.Children(0)
            Return group
        End Function

        Private Shared Function ParseSvg(svg As String) As SvgIconSource
            Dim viewBoxMatch = Regex.Match(svg, "viewBox=""(?<x>[-0-9.]+)\s+(?<y>[-0-9.]+)\s+(?<w>[-0-9.]+)\s+(?<h>[-0-9.]+)""")
            If Not viewBoxMatch.Success Then Throw New InvalidDataException("Unsupported SVG icon.")

            Dim viewBox = New Rect(
                ParseInvariant(viewBoxMatch.Groups("x").Value),
                ParseInvariant(viewBoxMatch.Groups("y").Value),
                ParseInvariant(viewBoxMatch.Groups("w").Value),
                ParseInvariant(viewBoxMatch.Groups("h").Value))

            Dim transform = Matrix.Identity
            Dim transformMatch = Regex.Match(svg, "transform=""translate\((?<tx>[-0-9.]+),(?<ty>[-0-9.]+)\)\s+scale\((?<sx>[-0-9.]+),(?<sy>[-0-9.]+)\)""")
            If transformMatch.Success Then
                transform = Matrix.CreateScale(
                                ParseInvariant(transformMatch.Groups("sx").Value),
                                ParseInvariant(transformMatch.Groups("sy").Value)) *
                            Matrix.CreateTranslation(
                                ParseInvariant(transformMatch.Groups("tx").Value),
                                ParseInvariant(transformMatch.Groups("ty").Value))
            End If

            Dim rootTagMatch = Regex.Match(svg, "<svg\b[^>]*>", RegexOptions.Singleline)
            Dim rootTag = If(rootTagMatch.Success, rootTagMatch.Value, "")
            Dim isStroked = Regex.IsMatch(rootTag, "fill\s*=\s*""none""", RegexOptions.IgnoreCase)
            Dim strokeWidth = 10.0
            Dim strokeWidthMatch = Regex.Match(rootTag, "stroke-width\s*=\s*""(?<w>[-0-9.]+)""")
            If strokeWidthMatch.Success Then strokeWidth = ParseInvariant(strokeWidthMatch.Groups("w").Value)

            Dim pathData = ParseShapes(svg)
            If pathData.Count = 0 Then Throw New InvalidDataException("Unsupported SVG icon.")

            Return New SvgIconSource With {
                .ViewBox = viewBox,
                .SvgTransform = transform,
                .PathData = pathData,
                .IsStroked = isStroked,
                .StrokeWidth = strokeWidth
            }
        End Function

        ' Sammelt die Pfaddaten aller Grundformen (path/rect/circle/ellipse/line) einer SVG-Datei, da
        ' manche Icons (v.a. die Kontur-Symbole) aus mehreren Elementen statt nur einem <path> bestehen.
        ' Reine Zeichenkettenarbeit - die Geometrie daraus baut BuildGeometry auf dem UI-Faden.
        Private Shared Function ParseShapes(svg As String) As List(Of String)
            Dim shapeRegex = New Regex("<(?<tag>path|rect|circle|ellipse|line)\b(?<attrs>[^>]*?)/?>", RegexOptions.Singleline)
            Dim pathData As New List(Of String)()

            For Each m As Match In shapeRegex.Matches(svg)
                Dim d As String = Nothing
                Dim attrs = m.Groups("attrs").Value
                Select Case m.Groups("tag").Value
                    Case "path" : d = GetAttr(attrs, "d")
                    Case "rect" : d = RectToPath(attrs)
                    Case "circle" : d = CircleToPath(attrs)
                    Case "ellipse" : d = EllipseToPath(attrs)
                    Case "line" : d = LineToPath(attrs)
                End Select

                If Not String.IsNullOrWhiteSpace(d) Then pathData.Add(d)
            Next

            Return pathData
        End Function

        Private Shared Function GetAttr(attrs As String, name As String) As String
            Dim m = Regex.Match(attrs, name & "\s*=\s*""(?<v>[^""]*)""")
            Return If(m.Success, m.Groups("v").Value, Nothing)
        End Function

        Private Shared Function GetAttrNumber(attrs As String, name As String, fallback As Double) As Double
            Dim v = GetAttr(attrs, name)
            If v Is Nothing Then Return fallback
            Return ParseInvariant(v)
        End Function

        Private Shared Function RectToPath(attrs As String) As String
            Dim x = GetAttrNumber(attrs, "x", 0)
            Dim y = GetAttrNumber(attrs, "y", 0)
            Dim w = GetAttrNumber(attrs, "width", 0)
            Dim h = GetAttrNumber(attrs, "height", 0)
            If w <= 0 OrElse h <= 0 Then Return Nothing

            Dim rx = GetAttrNumber(attrs, "rx", GetAttrNumber(attrs, "ry", 0))
            If rx <= 0 Then
                Return $"M{Inv(x)},{Inv(y)} H{Inv(x + w)} V{Inv(y + h)} H{Inv(x)} Z"
            End If
            rx = Math.Min(rx, Math.Min(w / 2, h / 2))

            Return $"M{Inv(x + rx)},{Inv(y)} " &
                   $"H{Inv(x + w - rx)} A{Inv(rx)},{Inv(rx)} 0 0 1 {Inv(x + w)},{Inv(y + rx)} " &
                   $"V{Inv(y + h - rx)} A{Inv(rx)},{Inv(rx)} 0 0 1 {Inv(x + w - rx)},{Inv(y + h)} " &
                   $"H{Inv(x + rx)} A{Inv(rx)},{Inv(rx)} 0 0 1 {Inv(x)},{Inv(y + h - rx)} " &
                   $"V{Inv(y + rx)} A{Inv(rx)},{Inv(rx)} 0 0 1 {Inv(x + rx)},{Inv(y)} Z"
        End Function

        Private Shared Function CircleToPath(attrs As String) As String
            Dim cx = GetAttrNumber(attrs, "cx", 0)
            Dim cy = GetAttrNumber(attrs, "cy", 0)
            Dim r = GetAttrNumber(attrs, "r", 0)
            If r <= 0 Then Return Nothing
            Return $"M{Inv(cx - r)},{Inv(cy)} A{Inv(r)},{Inv(r)} 0 1 0 {Inv(cx + r)},{Inv(cy)} A{Inv(r)},{Inv(r)} 0 1 0 {Inv(cx - r)},{Inv(cy)} Z"
        End Function

        Private Shared Function EllipseToPath(attrs As String) As String
            Dim cx = GetAttrNumber(attrs, "cx", 0)
            Dim cy = GetAttrNumber(attrs, "cy", 0)
            Dim rx = GetAttrNumber(attrs, "rx", 0)
            Dim ry = GetAttrNumber(attrs, "ry", 0)
            If rx <= 0 OrElse ry <= 0 Then Return Nothing
            Return $"M{Inv(cx - rx)},{Inv(cy)} A{Inv(rx)},{Inv(ry)} 0 1 0 {Inv(cx + rx)},{Inv(cy)} A{Inv(rx)},{Inv(ry)} 0 1 0 {Inv(cx - rx)},{Inv(cy)} Z"
        End Function

        Private Shared Function LineToPath(attrs As String) As String
            Dim x1 = GetAttrNumber(attrs, "x1", 0)
            Dim y1 = GetAttrNumber(attrs, "y1", 0)
            Dim x2 = GetAttrNumber(attrs, "x2", 0)
            Dim y2 = GetAttrNumber(attrs, "y2", 0)
            Return $"M{Inv(x1)},{Inv(y1)} L{Inv(x2)},{Inv(y2)}"
        End Function

        Private Shared Function Inv(value As Double) As String
            Return value.ToString(CultureInfo.InvariantCulture)
        End Function

        Private Shared Function ParseInvariant(value As String) As Double
            Return Double.Parse(value, CultureInfo.InvariantCulture)
        End Function

        Private Shared Function GetUniformFit(sourceSize As Size, targetSize As Size) As IconFit
            If sourceSize.Width <= 0 OrElse sourceSize.Height <= 0 Then Return New IconFit()
            Dim scale = Math.Min(targetSize.Width / sourceSize.Width, targetSize.Height / sourceSize.Height)
            Return New IconFit With {
                .Scale = scale,
                .Width = sourceSize.Width * scale,
                .Height = sourceSize.Height * scale
            }
        End Function

        Private Class SvgIconData
            Public Property ViewBox As Rect
            Public Property SvgTransform As Matrix
            Public Property Geometry As Geometry
            Public Property IsStroked As Boolean
            Public Property StrokeWidth As Double
        End Class

        ''' Zwischenstufe ohne Avalonia-Objekte: darf auf jedem Faden entstehen (siehe LoadSource).
        Private Class SvgIconSource
            Public Property ViewBox As Rect
            Public Property SvgTransform As Matrix
            Public Property PathData As List(Of String)
            Public Property IsStroked As Boolean
            Public Property StrokeWidth As Double
        End Class

        Private Structure IconFit
            Public Property Scale As Double
            Public Property Width As Double
            Public Property Height As Double
        End Structure

    End Class

End Namespace
