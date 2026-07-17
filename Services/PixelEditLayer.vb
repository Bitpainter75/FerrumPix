Imports Avalonia
Imports SkiaSharp

Namespace Services

    Public NotInheritable Class PixelPaintOptions
        Public Property Kind As String = "Brush"
        Public Property StrokeColor As String = "#FF000000"
        Public Property EraserFillColor As String = ""
        Public Property StrokeWidth As Single = 24
        Public Property Opacity As Single = 100
        Public Property FlowPercent As Single = 100
        Public Property HardnessPercent As Single = 100
        Public Property BrushPreset As String = "soft"
        Public Property ShadowEnabled As Boolean = False
        Public Property ShadowOffsetXPercent As Single = 2
        Public Property ShadowOffsetYPercent As Single = 2
        Public Property ShadowBlur As Single = 6
        Public Property ShadowStrength As Single = 100
        Public Property ShadowColor As String = "#80000000"
        Public Property ShadowSizePercent As Single = 100
        Public Property GlowEnabled As Boolean = False
        Public Property GlowBlur As Single = 10
        Public Property GlowStrength As Single = 100
        Public Property GlowColor As String = "#FFFFFF00"
    End Class

    Public NotInheritable Class PixelPaintResult
        Public Property Entry As PixelPaintStroke
        Public Property Stroke As BrushStroke
        Public Property DirtyRect As SKRectI
        ' Monoton wachsender Zähler dieses Layers NACH diesem Add. Historisch für den inkrementellen
        ' Composite-Fast-Path; der heutige RasterCompositeCache validiert stattdessen über einen
        ' Inhalts-Stamp (ImageProcessor.ComputeRasterStrokesStamp). Der Token bleibt als
        ' Sequenz-Diagnose erhalten; Clear/Load erhöhen ihn weiterhin.
        Public Property SequenceToken As Long
        ' Gesamtzahl der Strichsegmente VOR diesem Add - der Fast-Path prüft, dass der Composite-Cache
        ' genau so viele Segmente enthält.
        Public Property BaselineSegmentCount As Integer
        ' Nur Normalmischung ohne Schatten/Glühen darf inkrementell gebacken werden; sonst hängt das
        ' Ergebnis von der ganzen Strich-Silhouette bzw. vom Untergrund ab (siehe Notizen).
        Public Property FastEligible As Boolean
    End Class

    Public NotInheritable Class PixelEditLayer
        Private ReadOnly _strokes As New List(Of PixelPaintStroke)()
        Private _activeStroke As PixelPaintStroke = Nothing
        Private _activeStrokeIsEraser As Boolean = False
        Private _sequenceToken As Long = 0

        Public ReadOnly Property Count As Integer
            Get
                Return _strokes.Count
            End Get
        End Property

        ''' <summary>Aktueller Sequenz-Token, ohne einen Strich hinzuzufügen - für den Kein-Delta-Aufruf
        ''' des Composite-Caches (Objekt-Move), der prüft, ob das Composite exakt diesen Stand spiegelt.</summary>
        Public ReadOnly Property CurrentSequenceToken As Long
            Get
                Return _sequenceToken
            End Get
        End Property

        ''' <summary>Gesamtzahl der Strichsegmente über alle Raster-Einträge (siehe CurrentSequenceToken).</summary>
        Public ReadOnly Property CurrentSegmentCount As Integer
            Get
                Return TotalSegmentCount
            End Get
        End Property

        ''' <summary>Summe aller Strichsegmente über alle Raster-Einträge - der Composite-Cache spiegelt
        ''' genau diese Anzahl gezeichneter Segmente.</summary>
        Private ReadOnly Property TotalSegmentCount As Integer
            Get
                Dim total = 0
                For Each s In _strokes
                    If s?.Strokes IsNot Nothing Then total += s.Strokes.Count
                Next
                Return total
            End Get
        End Property

        Public Sub Clear()
            _strokes.Clear()
            ResetActiveStroke()
            ' Bricht die inkrementelle Kette: der nächste Add erzwingt einen Composite-Rebuild.
            _sequenceToken += 1
        End Sub

        Public Sub ResetActiveStroke()
            _activeStroke = Nothing
            _activeStrokeIsEraser = False
        End Sub

        Public Sub Load(strokes As IEnumerable(Of PixelPaintStroke))
            Clear()
            If strokes Is Nothing Then Return
            For Each stroke In strokes
                If stroke IsNot Nothing Then _strokes.Add(stroke.Clone())
            Next
        End Sub

        Public Function CloneStrokes() As List(Of PixelPaintStroke)
            Return _strokes.Select(Function(s) s.Clone()).ToList()
        End Function

        Public Function AddStroke(pixelPoints As IReadOnlyList(Of Point),
                                  options As PixelPaintOptions,
                                  sourceWidth As Integer,
                                  sourceHeight As Integer) As PixelPaintResult
            If pixelPoints Is Nothing OrElse pixelPoints.Count < 2 OrElse options Is Nothing Then Return Nothing

            Dim baselineSegmentCount = TotalSegmentCount

            Dim minX = Math.Max(0, pixelPoints.Min(Function(p) p.X))
            Dim minY = Math.Max(0, pixelPoints.Min(Function(p) p.Y))
            Dim maxX = Math.Min(sourceWidth, pixelPoints.Max(Function(p) p.X))
            Dim maxY = Math.Min(sourceHeight, pixelPoints.Max(Function(p) p.Y))
            Dim newStroke = New BrushStroke(pixelPoints.Select(Function(p) New StrokePoint(CSng(p.X), CSng(p.Y))).ToList())

            Dim expectedKind = If(String.Equals(options.Kind, "Eraser", StringComparison.OrdinalIgnoreCase), "Eraser", "Brush")
            Dim isEraser = String.Equals(expectedKind, "Eraser", StringComparison.Ordinal)
            Dim target As PixelPaintStroke = Nothing

            If CanAppendToActiveStroke(options, expectedKind, isEraser) Then
                target = _activeStroke
                Dim unionMinX = Math.Min(target.XPixels, CSng(minX))
                Dim unionMinY = Math.Min(target.YPixels, CSng(minY))
                Dim unionMaxX = Math.Max(target.XPixels + target.WidthPixels, CSng(maxX))
                Dim unionMaxY = Math.Max(target.YPixels + target.HeightPixels, CSng(maxY))
                target.Strokes.Add(newStroke)
                target.XPixels = unionMinX
                target.YPixels = unionMinY
                target.WidthPixels = Math.Max(1, unionMaxX - unionMinX)
                target.HeightPixels = Math.Max(1, unionMaxY - unionMinY)
            Else
                target = CreateStrokeEntry(options, expectedKind, isEraser, newStroke, minX, minY, maxX, maxY)
                _strokes.Add(target)
                _activeStroke = target
                _activeStrokeIsEraser = isEraser
            End If

            Dim dirty = ImageProcessor.ComputePixelPaintDirtyRect(sourceWidth, sourceHeight, target, newStroke)
            _sequenceToken += 1
            Dim fastEligible = (Not target.ShadowEnabled) AndAlso (Not target.GlowEnabled) AndAlso
                               String.Equals(If(target.BlendMode, "Normal").Trim(), "Normal", StringComparison.OrdinalIgnoreCase)
            Return New PixelPaintResult With {
                .Entry = target,
                .Stroke = newStroke,
                .DirtyRect = dirty,
                .SequenceToken = _sequenceToken,
                .BaselineSegmentCount = baselineSegmentCount,
                .FastEligible = fastEligible
            }
        End Function

        Private Function CanAppendToActiveStroke(options As PixelPaintOptions, expectedKind As String, isEraser As Boolean) As Boolean
            Return _activeStroke IsNot Nothing AndAlso
                   _activeStrokeIsEraser = isEraser AndAlso
                   String.Equals(_activeStroke.Kind, expectedKind, StringComparison.OrdinalIgnoreCase) AndAlso
                   _strokes.Contains(_activeStroke) AndAlso
                   Math.Abs(_activeStroke.StrokeWidth - options.StrokeWidth) < 0.001F AndAlso
                   Math.Abs(_activeStroke.Opacity - options.Opacity) < 0.001F AndAlso
                   Math.Abs(_activeStroke.FlowPercent - options.FlowPercent) < 0.001F AndAlso
                   Math.Abs(_activeStroke.HardnessPercent - options.HardnessPercent) < 0.001F AndAlso
                   String.Equals(_activeStroke.StrokeColor, options.StrokeColor, StringComparison.OrdinalIgnoreCase) AndAlso
                   String.Equals(_activeStroke.BrushPreset, options.BrushPreset, StringComparison.Ordinal) AndAlso
                   (Not isEraser OrElse String.Equals(_activeStroke.EraserFillColor, options.EraserFillColor, StringComparison.OrdinalIgnoreCase))
        End Function

        ''' <summary>ARBEITSBILD-Umbau (Stufe D): baut einen eigenständigen, TRANSIENTEN Strich -
        ''' keine Persistenz mehr, der Strich wird sofort ins Arbeitsbild eingebacken und die Pixel
        ''' sind ab dem Commit die Quelle der Wahrheit. <paramref name="dirtyRect"/> = betroffene
        ''' Region in Basis-Bildpixeln inkl. Breiten-/Schatten-/Glüh-Rand. Nothing bei ungültiger
        ''' Eingabe.</summary>
        Public Shared Function CreateTransientStroke(pixelPoints As IReadOnlyList(Of Point),
                                                     options As PixelPaintOptions,
                                                     sourceWidth As Integer, sourceHeight As Integer,
                                                     ByRef dirtyRect As SKRectI) As PixelPaintStroke
            dirtyRect = SKRectI.Empty
            If pixelPoints Is Nothing OrElse pixelPoints.Count < 2 OrElse options Is Nothing Then Return Nothing
            Dim minX = Math.Max(0, pixelPoints.Min(Function(p) p.X))
            Dim minY = Math.Max(0, pixelPoints.Min(Function(p) p.Y))
            Dim maxX = Math.Min(sourceWidth, pixelPoints.Max(Function(p) p.X))
            Dim maxY = Math.Min(sourceHeight, pixelPoints.Max(Function(p) p.Y))
            Dim newStroke = New BrushStroke(pixelPoints.Select(Function(p) New StrokePoint(CSng(p.X), CSng(p.Y))).ToList())
            Dim expectedKind = If(String.Equals(options.Kind, "Eraser", StringComparison.OrdinalIgnoreCase), "Eraser", "Brush")
            Dim isEraser = String.Equals(expectedKind, "Eraser", StringComparison.Ordinal)
            Dim entry = CreateStrokeEntry(options, expectedKind, isEraser, newStroke, minX, minY, maxX, maxY)
            dirtyRect = ImageProcessor.ComputePixelPaintDirtyRect(sourceWidth, sourceHeight, entry, newStroke)
            Return entry
        End Function

        Private Shared Function CreateStrokeEntry(options As PixelPaintOptions,
                                                  expectedKind As String,
                                                  isEraser As Boolean,
                                                  stroke As BrushStroke,
                                                  minX As Double,
                                                  minY As Double,
                                                  maxX As Double,
                                                  maxY As Double) As PixelPaintStroke
            Return New PixelPaintStroke With {
                .Kind = expectedKind,
                .Strokes = New List(Of BrushStroke) From {stroke},
                .XPixels = CSng(minX),
                .YPixels = CSng(minY),
                .WidthPixels = CSng(Math.Max(1, maxX - minX)),
                .HeightPixels = CSng(Math.Max(1, maxY - minY)),
                .StrokeColor = options.StrokeColor,
                .EraserFillColor = If(isEraser, options.EraserFillColor, ""),
                .StrokeWidth = options.StrokeWidth,
                .Opacity = options.Opacity,
                .BlendMode = "Normal",
                .FlowPercent = options.FlowPercent,
                .HardnessPercent = options.HardnessPercent,
                .BrushPreset = If(isEraser, "soft", options.BrushPreset),
                .ShadowEnabled = (Not isEraser) AndAlso options.ShadowEnabled,
                .ShadowOffsetXPercent = options.ShadowOffsetXPercent,
                .ShadowOffsetYPercent = options.ShadowOffsetYPercent,
                .ShadowBlur = options.ShadowBlur,
                .ShadowStrength = options.ShadowStrength,
                .ShadowColor = options.ShadowColor,
                .ShadowSizePercent = options.ShadowSizePercent,
                .GlowEnabled = (Not isEraser) AndAlso options.GlowEnabled,
                .GlowBlur = options.GlowBlur,
                .GlowStrength = options.GlowStrength,
                .GlowColor = options.GlowColor
            }
        End Function
    End Class
End Namespace
