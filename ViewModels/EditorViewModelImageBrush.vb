Imports System
Imports System.Collections.Generic
Imports System.Linq
Imports ReactiveUI
Imports SkiaSharp
Imports FerrumPix.Services

Namespace ViewModels

    ''' <summary>Der BILDPINSEL: ein Werkzeug fuer alles, was mit dem Pinsel das vorhandene Bild
    ''' veraendert, statt Farbe aufzutragen - Radieren, Abwedeln, Nachbelichten, Schwamm und Farbe
    ''' ersetzen. In der Werkzeugleiste EIN Knopf: der Malpinsel bleibt allein, die uebrigen Arten
    ''' stehen zusammen, statt je einen eigenen Platz zu belegen.
    '''
    ''' Intern ist es dasselbe Zeichenwerkzeug wie der Pinsel (EditorTool.Draw). Der Radierer
    ''' behaelt seinen eigenen Weg (_isEraserMode, AddBrushStrokeCore); die vier uebrigen Arten
    ''' stehen in _toneMode und gehen durch AddToneStroke: der Strich liefert nur die Deckung, und
    ''' ImageProcessor.ApplyToneBrush rechnet darunter je Bildpunkt.</summary>
    Partial Public Class EditorViewModel

        ''' <summary>Die Art des Bildpinsels neben dem Radierer: "Dodge", "Burn", "Sponge",
        ''' "ReplaceColor", oder leer (Pinsel oder Radierer).</summary>
        Private _toneMode As String = ""
        ''' <summary>Die zuletzt benutzte Art, damit der Knopf in der Leiste dorthin zurueckfuehrt.</summary>
        Private _lastImageBrushMode As String = "Eraser"
        Private _toneRange As String = "Midtones"
        Private _spongeSaturate As Boolean = False
        ''' <summary>Abwedeln und Nachbelichten nach der Helligkeit, mit gleichem Faktor fuer alle
        ''' Kanaele, sodass Farbton, Saettigung, Schwarz und Weiss bleiben. Ab Werk an, wie beim
        ''' Referenzprogramm.</summary>
        Private _protectTones As Boolean = True
        Private _replaceColor As String = "#FF3A7BD5"

        ''' <summary>Die Arten neben dem Radierer. "RedEye" ist dabei, rechnet aber nicht mit einem Zug:
        ''' ein Klick entfernt rote Augen im Pinselkreis (RemoveRedEyeAt). Frueher war das ein Schalter
        ''' im Retusche-Werkzeug; hier steht es bei den Arten, die das vorhandene Bild aendern.</summary>
        Private Shared ReadOnly ToneModes As String() = {"Dodge", "Burn", "Sponge", "ReplaceColor", "RedEye", "Restore",
                                                         "BackgroundEraser", "MagicEraser"}

        ''' <summary>Was ein Zug des Bildpinsels zum Umrechnen braucht, auf dem UI-Faden
        ''' eingefroren: der Hintergrund darf die Felder des Werkzeugs nicht lesen, und wer mitten im
        ''' Zug umstellt, meint den naechsten.</summary>
        Friend NotInheritable Class ToneBrushSettings
            Public Property Kind As String
            Public Property Range As String
            Public Property Saturate As Boolean
            Public Property Color As SKColor
            Public Property ProtectTones As Boolean = True
            ''' <summary>Die beiden Radierer nach Farbe: Toleranz 0 bis 1 und ob nur zaehlt, was
            ''' zusammenhaengt.</summary>
            Public Property Tolerance As Single
            Public Property Contiguous As Boolean = True
            ''' <summary>Hintergrund-Radierer: die Farbe am Ansatz des Zuges gilt fuer den ganzen Zug.</summary>
            Public Property SampleOnce As Boolean = True
            ''' <summary>Die Punkte des Zuges in Bildpunkten des ZIELBILDS (Arbeitsbild oder Bild der
            ''' Ebene) und der Radius des Pinsels dort. Der Hintergrund-Radierer braucht sie fuer
            ''' seine Tupfer, der magische Radierer den ersten als Klickpunkt. Gesetzt von dem, der
            ''' den Zug ins Zielbild umrechnet.</summary>
            Public Property StrokePoints As List(Of SKPoint)
            Public Property StrokeRadius As Single
        End Class

        Private Function CurrentToneBrushSettings() As ToneBrushSettings
            Dim replaceWith As SKColor
            If Not SKColor.TryParse(_replaceColor, replaceWith) Then replaceWith = New SKColor(58, 123, 213)
            Return New ToneBrushSettings With {.Kind = _toneMode, .Range = _toneRange,
                                               .Saturate = _spongeSaturate, .Color = replaceWith,
                                               .ProtectTones = _protectTones,
                                               .Tolerance = CSng(_eraserTolerance / 100.0),
                                               .Contiguous = _eraserContiguous,
                                               .SampleOnce = _eraserSampleOnce}
        End Function

        ' ── Hintergrund-Radierer und magischer Radierer ─────────────────────────
        '
        ' Zwei weitere Arten des Bildpinsels. Der Hintergrund-Radierer ist ein Zug und geht den Weg
        ' der uebrigen Arten (AddToneStroke), nur nimmt er Deckung weg, wo die Farbe der unter der
        ' Pinselmitte aehnelt. Der magische Radierer ist ein Klick wie Rote Augen (MagicEraseAt).
        ' Beide wirken im Foto und auf einer markierten Bild-Ebene und teilen sich Toleranz und
        ' den Haken "Zusammenhaengend"; gespeichert wird beides nicht.

        ' 15 wie beim Zauberstab. Mit 30 fiel an echten Fotos fast alles in Reichweite weg, auch
        ' Baeume und Daecher neben dem Himmel (Probe am Bestand).
        Private _eraserTolerance As Double = 15
        Private _eraserContiguous As Boolean = True
        ' Ab Werk an. Fortlaufend abgetastet nahm der Zug alles weg, worueber die Pinselmitte
        ' wanderte, und wirkte wie ein unsauberer Radierer (Nutzerbefund).
        Private _eraserSampleOnce As Boolean = True

        ''' <summary>Hintergrund-Radierer: die Farbe, auf der der Zug beginnt, gilt fuer den ganzen
        ''' Zug. Ohne den Haken nimmt jeder Tupfer die Farbe unter der Pinselmitte.</summary>
        Public Property EraserSampleOnce As Boolean
            Get
                Return _eraserSampleOnce
            End Get
            Set(value As Boolean)
                Me.RaiseAndSetIfChanged(_eraserSampleOnce, value)
            End Set
        End Property

        Public ReadOnly Property IsBackgroundEraserMode As Boolean
            Get
                Return _currentTool = EditorTool.Draw AndAlso _toneMode = "BackgroundEraser"
            End Get
        End Property

        ''' <summary>Der magische Radierer: ein Klick ins Bild ist ein fertiger Schritt, kein Zug.</summary>
        Public ReadOnly Property IsMagicEraserMode As Boolean
            Get
                Return _currentTool = EditorTool.Draw AndAlso _toneMode = "MagicEraser"
            End Get
        End Property

        ''' <summary>Einer der beiden Radierer nach Farbe: blendet Toleranz und "Zusammenhaengend" ein.</summary>
        Public ReadOnly Property IsColorEraserMode As Boolean
            Get
                Return IsBackgroundEraserMode OrElse IsMagicEraserMode
            End Get
        End Property

        ''' <summary>Eine Art, die mit einem Klick arbeitet (Rote Augen, magischer Radierer): die
        ''' Regler eines Zuges (Haerte, Staerke, Fluss, Glaetten) haben dort nichts zu stellen.</summary>
        Public ReadOnly Property IsClickBrushMode As Boolean
            Get
                Return IsRedEyeMode OrElse IsMagicEraserMode
            End Get
        End Property

        ''' <summary>Der magische Radierer hat keinen Pinselkreis: sein Klick nimmt die ganze Flaeche
        ''' aehnlicher Farbe, unbegrenzt. Der Groessenregler ist dort ausgeblendet, der Kreis unter
        ''' dem Zeiger ebenso, und Mausrad und Tasten stellen nichts (EditorView).</summary>
        Public ReadOnly Property ShowImageBrushSize As Boolean
            Get
                Return Not IsMagicEraserMode
            End Get
        End Property

        ''' <summary>Wie weit eine Farbe abweichen darf, in Prozent (0..100), auf der Skala des
        ''' Zauberstabs. Beim Hintergrund-Radierer gegen die Farbe unter der Pinselmitte, beim
        ''' magischen gegen die angeklickte.</summary>
        Public Property EraserTolerance As Double
            Get
                Return _eraserTolerance
            End Get
            Set(value As Double)
                Me.RaiseAndSetIfChanged(_eraserTolerance, Math.Max(0, Math.Min(100, value)))
            End Set
        End Property

        ''' <summary>Nur was zusammenhaengt: beim Hintergrund-Radierer mit der Pinselmitte, beim
        ''' magischen mit dem Klick. Ohne den Haken zaehlt jede passende Farbe unter der Spitze
        ''' bzw. im ganzen Bild.</summary>
        Public Property EraserContiguous As Boolean
            Get
                Return _eraserContiguous
            End Get
            Set(value As Boolean)
                Me.RaiseAndSetIfChanged(_eraserContiguous, value)
            End Set
        End Property

        ''' <summary>Die Art, wenn der Bildpinsel aktiv ist ("Eraser" oder eine der vier), sonst leer.</summary>
        Public ReadOnly Property ImageBrushMode As String
            Get
                If _currentTool <> EditorTool.Draw Then Return ""
                If _toneMode <> "" Then Return _toneMode
                If _isEraserMode Then Return "Eraser"
                Return ""
            End Get
        End Property

        Public ReadOnly Property IsImageBrushMode As Boolean
            Get
                Return ImageBrushMode <> ""
            End Get
        End Property

        ''' <summary>Eine der vier Arten, die das Bild mit einem Zug umrechnen (nicht der Radierer,
        ''' nicht Rote Augen).</summary>
        Public ReadOnly Property IsToneBrushMode As Boolean
            Get
                Return _currentTool = EditorTool.Draw AndAlso _toneMode <> "" AndAlso _toneMode <> "RedEye" AndAlso
                       _toneMode <> "MagicEraser"
            End Get
        End Property

        ''' <summary>Rote Augen: ein Klick ins Bild ist ein fertiger Schritt, kein Zug.</summary>
        Public ReadOnly Property IsRedEyeMode As Boolean
            Get
                Return _currentTool = EditorTool.Draw AndAlso _toneMode = "RedEye"
            End Get
        End Property

        ''' <summary>Der Protokollpinsel: malt in die Maske einer Protokollebene (AddRestoreStroke).</summary>
        Public ReadOnly Property IsRestoreBrushMode As Boolean
            Get
                Return IsToneBrushMode AndAlso _toneMode = "Restore"
            End Get
        End Property

        Public ReadOnly Property IsDodgeBurnMode As Boolean
            Get
                Return IsToneBrushMode AndAlso (_toneMode = "Dodge" OrElse _toneMode = "Burn")
            End Get
        End Property

        Public ReadOnly Property IsSpongeMode As Boolean
            Get
                Return IsToneBrushMode AndAlso _toneMode = "Sponge"
            End Get
        End Property

        Public ReadOnly Property IsReplaceColorMode As Boolean
            Get
                Return IsToneBrushMode AndAlso _toneMode = "ReplaceColor"
            End Get
        End Property

        ''' <summary>Auf welchen Tonbereich Abwedeln und Nachbelichten vor allem wirken.</summary>
        Public Property ToneRangeShadows As Boolean
            Get
                Return _toneRange = "Shadows"
            End Get
            Set(value As Boolean)
                If value Then SetToneRange("Shadows")
            End Set
        End Property

        Public Property ToneRangeMidtones As Boolean
            Get
                Return _toneRange = "Midtones"
            End Get
            Set(value As Boolean)
                If value Then SetToneRange("Midtones")
            End Set
        End Property

        Public Property ToneRangeHighlights As Boolean
            Get
                Return _toneRange = "Highlights"
            End Get
            Set(value As Boolean)
                If value Then SetToneRange("Highlights")
            End Set
        End Property

        Private Sub SetToneRange(range As String)
            If _toneRange = range Then Return
            _toneRange = range
            Me.RaisePropertyChanged(NameOf(ToneRangeShadows))
            Me.RaisePropertyChanged(NameOf(ToneRangeMidtones))
            Me.RaisePropertyChanged(NameOf(ToneRangeHighlights))
        End Sub

        ''' <summary>„Toene schuetzen" fuer Abwedeln und Nachbelichten (ImageProcessor.DodgeBurnShift).</summary>
        Public Property ToneProtect As Boolean
            Get
                Return _protectTones
            End Get
            Set(value As Boolean)
                If _protectTones = value Then Return
                _protectTones = value
                Me.RaisePropertyChanged(NameOf(ToneProtect))
            End Set
        End Property

        ''' <summary>Schwamm: saettigen statt entsaettigen. Ab Werk entsaettigt er, wie meist gemeint.</summary>
        Public Property SpongeSaturate As Boolean
            Get
                Return _spongeSaturate
            End Get
            Set(value As Boolean)
                If _spongeSaturate = value Then Return
                _spongeSaturate = value
                Me.RaisePropertyChanged(NameOf(SpongeSaturate))
                Me.RaisePropertyChanged(NameOf(SpongeDesaturate))
            End Set
        End Property

        Public Property SpongeDesaturate As Boolean
            Get
                Return Not _spongeSaturate
            End Get
            Set(value As Boolean)
                SpongeSaturate = Not value
            End Set
        End Property

        ''' <summary>Die Farbe, die "Farbe ersetzen" einsetzt. Nur Farbton und Saettigung zaehlen,
        ''' die Helligkeit bleibt die des Bildes.</summary>
        Public Property ReplaceColorValue As Avalonia.Media.Color
            Get
                Return ParseAvaloniaColorOrDefault(_replaceColor, Avalonia.Media.Color.Parse("#FF3A7BD5"))
            End Get
            Set(value As Avalonia.Media.Color)
                Dim text = ColorToHex(value)
                If String.Equals(text, _replaceColor, StringComparison.OrdinalIgnoreCase) Then Return
                _replaceColor = text
                Me.RaisePropertyChanged(NameOf(ReplaceColorValue))
            End Set
        End Property

        ''' <summary>Stellt die Art des Bildpinsels ein. Gerufen aus SetPaintMode; der Radierer wird
        ''' dort ueber IsEraserMode geschaltet, die vier uebrigen hier.</summary>
        Private Sub SetToneMode(mode As String)
            Dim normalized = If(ToneModes.Contains(mode), mode, "")
            If _toneMode = normalized Then Return
            _toneMode = normalized
            If normalized <> "" Then _lastImageBrushMode = normalized
            RaiseImageBrushModeChanged()
        End Sub

        Private Sub RaiseImageBrushModeChanged()
            Me.RaisePropertyChanged(NameOf(ImageBrushMode))
            Me.RaisePropertyChanged(NameOf(IsImageBrushMode))
            Me.RaisePropertyChanged(NameOf(IsToneBrushMode))
            Me.RaisePropertyChanged(NameOf(IsDodgeBurnMode))
            Me.RaisePropertyChanged(NameOf(IsSpongeMode))
            Me.RaisePropertyChanged(NameOf(IsReplaceColorMode))
            Me.RaisePropertyChanged(NameOf(IsRestoreBrushMode))
            Me.RaisePropertyChanged(NameOf(IsRedEyeMode))
            Me.RaisePropertyChanged(NameOf(IsBackgroundEraserMode))
            Me.RaisePropertyChanged(NameOf(IsMagicEraserMode))
            Me.RaisePropertyChanged(NameOf(IsColorEraserMode))
            Me.RaisePropertyChanged(NameOf(IsClickBrushMode))
            Me.RaisePropertyChanged(NameOf(ShowImageBrushSize))
            Me.RaisePropertyChanged(NameOf(IsBrushPaintMode))
            Me.RaisePropertyChanged(NameOf(IsEraserPaintMode))
            Me.RaisePropertyChanged(NameOf(ShowBrushStrokeAdjustments))
            Me.RaisePropertyChanged(NameOf(CurrentToolLabel))
            Me.RaisePropertyChanged(NameOf(CurrentToolIconSource))
            Me.RaisePropertyChanged(NameOf(SelectedPaintMode))
        End Sub

        ''' <summary>Der Name der aktiven Art fuer Werkzeugtitel und Rueckgaengig-Eintrag. Jeder Text
        ''' als eigenes T("..."), sonst sieht ihn die Sprachpruefung nicht.</summary>
        Private Function ToneModeLabel() As String
            Select Case _toneMode
                Case "Dodge" : Return LocalizationService.T("Abwedeln")
                Case "Burn" : Return LocalizationService.T("Nachbelichten")
                Case "Sponge" : Return LocalizationService.T("Schwamm")
                Case "ReplaceColor" : Return LocalizationService.T("Farbe ersetzen")
                Case "RedEye" : Return LocalizationService.T("Rote Augen entfernen")
                Case "Restore" : Return LocalizationService.T("Protokollpinsel")
                Case "BackgroundEraser" : Return LocalizationService.T("Hintergrund-Radierer")
                Case "MagicEraser" : Return LocalizationService.T("Magischer Radierer")
                Case Else : Return ""
            End Select
        End Function

        Private Function ToneModeIcon() As String
            Select Case _toneMode
                Case "Dodge" : Return "sun.svg"
                Case "Burn" : Return "sun-dim.svg"
                Case "Sponge" : Return "drop-half.svg"
                Case "ReplaceColor" : Return "swap.svg"
                Case "RedEye" : Return "eye.svg"
                Case "Restore" : Return "clock-counter-clockwise.svg"
                Case "BackgroundEraser" : Return "selection-background.svg"
                Case "MagicEraser" : Return "sparkle.svg"
                Case Else : Return "eraser.svg"
            End Select
        End Function

        ''' <summary>Ein Zug mit einer der vier Arten, die das Bild umrechnen. Derselbe Weg wie ein
        ''' Pinselstrich aufs Foto (AddBrushStrokeCore): Punkte ins Arbeitsbild umrechnen, Umriss
        ''' bilden, an der Auswahl begrenzen, regional ins Arbeitsbild backen, mit Rueckgaengig. Nur
        ''' gezeichnet wird nicht der Strich, sondern seine Deckung gerechnet und darunter umgerechnet.
        '''
        ''' AUF EINER MARKIERTEN BILD-EBENE wirkt der Zug in ihre Bildpunkte, ueber denselben Weg wie
        ''' Pinsel und Radierer dort (TryPaintStrokeIntoImageAnnotation mit den Einstellungen des
        ''' Bildpinsels). Steht ein Ziel fest, bleibt es dabei, auch wenn es scheitert - ein Rueckfall
        ''' aufs Foto saesse in den falschen Pixeln.</summary>
        Private Sub AddToneStroke(normalized As List(Of Avalonia.Point))
            ' Rote Augen kennen keinen Zug; der Klick geht in der Ansicht an RemoveRedEyeAt.
            If _toneMode = "RedEye" Then Return
            ' Der magische Radierer ebenso: der Klick geht an MagicEraseAt.
            If _toneMode = "MagicEraser" Then Return
            If _toneMode = "Restore" Then
                AddRestoreStroke(normalized)
                Return
            End If
            Dim imageObject = FindStrokeTargetImageAnnotation()
            If imageObject IsNot Nothing Then
                If Not TryPaintStrokeIntoImageAnnotation(imageObject, normalized, isEraser:=False,
                                                         tone:=CurrentToneBrushSettings(), toneLabel:=ToneModeLabel()) Then
                    StatusText = LocalizationService.T("Malen fehlgeschlagen")
                End If
                Return
            End If

            Dim baseW = GetBaseWidth()
            Dim baseH = GetBaseHeight()
            Dim mapped = MapStrokeToWorkingPixels(normalized)
            If mapped.Points.Count < 2 Then Return
            Dim erasesByColor = _toneMode = "BackgroundEraser"

            ' Der Umriss mit Groesse, Haerte, Deckkraft und Fluss des Werkzeugs, in deckendem Weiss:
            ' gebraucht wird nur seine Deckung. Schatten und Schein gehoeren dem Malpinsel.
            Dim options = BuildPixelPaintOptions(False)
            options.StrokeColor = "#FFFFFFFF"
            options.BrushPreset = "soft"
            options.ShadowEnabled = False
            options.GlowEnabled = False
            Dim dirtyFull As SKRectI
            Dim stroke = PixelEditLayer.CreateTransientStroke(mapped.Points, options, baseW, baseH, dirtyFull, mapped.Pressures)
            If stroke Is Nothing OrElse dirtyFull.Width <= 0 OrElse dirtyFull.Height <= 0 Then Return
            dirtyFull = ClampRectToBitmap(dirtyFull, baseW, baseH)
            If dirtyFull.Width <= 0 OrElse dirtyFull.Height <= 0 Then Return

            Dim anyCoverage = False
            Dim selectionCoverage = BuildSelectionCoverageForWorkingRect(dirtyFull, anyCoverage)
            If selectionCoverage IsNot Nothing AndAlso Not anyCoverage Then
                selectionCoverage.Dispose()
                Return
            End If

            PushUndo(ToneModeLabel())
            Dim undoEntry = _lastPushedUndoEntry
            Dim renderAnn = stroke.ToRenderAnnotation()
            Dim tone = CurrentToneBrushSettings()
            If erasesByColor Then
                tone.StrokePoints = mapped.Points.Select(Function(p) New SKPoint(CSng(p.X), CSng(p.Y))).ToList()
                tone.StrokeRadius = CSng(Math.Max(1.0, options.StrokeWidth / 2.0))
            End If

            _hasChanges = True
            RaiseResetButtonStateChanged()
            _previewTimer.Stop()
            _previewPending = False

            EnqueueWorkingCommit(
                Function()
                    Try
                        Return _workingImage.CommitRegion(dirtyFull,
                            Sub(full)
                                Dim before As SKBitmap = Nothing
                                If selectionCoverage IsNot Nothing Then before = ImageProcessor.CopyRegion(full, dirtyFull)
                                Try
                                    Dim coverage = ImageProcessor.BuildStrokeCoverage(renderAnn, dirtyFull, full.Width, full.Height)
                                    If coverage Is Nothing OrElse Not ApplyToneBrushKind(full, dirtyFull, coverage, tone) Then
                                        Throw New InvalidOperationException("Bildpinsel konnte nicht angewendet werden")
                                    End If
                                    If selectionCoverage IsNot Nothing AndAlso
                                       Not ImageProcessor.RestoreOutsideCoverage(full, before, selectionCoverage, dirtyFull) Then
                                        Throw New InvalidOperationException("Auswahlgrenze konnte nicht angewendet werden")
                                    End If
                                Finally
                                    before?.Dispose()
                                End Try
                            End Sub, punchesAlpha:=erasesByColor)
                    Finally
                        selectionCoverage?.Dispose()
                    End Try
                End Function,
                Sub(patch)
                    If patch Is Nothing Then
                        StatusText = LocalizationService.T("Malen fehlgeschlagen")
                        SchedulePreviewUpdate()
                        Return
                    End If
                    If undoEntry IsNot Nothing Then undoEntry.Patch = patch
                    ' Das Arbeitsbild hat jetzt Loecher: der Grund hinter dem Bild muss das
                    ' Schachbrett zeigen, wie nach dem Radiergummi. Ohne diese Meldung erschien der
                    ' Ausschnitt erst beim naechsten Werkzeugwechsel (Nutzerbefund).
                    If erasesByColor Then Me.RaisePropertyChanged(NameOf(TransparencyBackgroundBrush))
                    SchedulePreviewUpdate()
                End Sub)
        End Sub

        ''' <summary>Rechnet eine Art des Bildpinsels unter der Deckung eines Zuges, im Foto wie im
        ''' Bild einer Ebene. EINE Stelle fuer die Weiche zwischen den Arten, die Farben umrechnen
        ''' (ImageProcessor.ApplyToneBrush), und dem Hintergrund-Radierer, der Deckung wegnimmt.</summary>
        Friend Shared Function ApplyToneBrushKind(target As SKBitmap, region As SKRectI, coverage As Byte(),
                                                  tone As ToneBrushSettings) As Boolean
            If tone Is Nothing Then Return False
            If tone.Kind = "BackgroundEraser" Then
                Return ImageProcessor.ApplyBackgroundEraser(target, region, coverage, tone.StrokePoints, tone.StrokeRadius,
                                                            tone.Tolerance, tone.Contiguous, tone.SampleOnce)
            End If
            Return ImageProcessor.ApplyToneBrush(target, region, coverage, tone.Kind, tone.Range,
                                                 tone.Saturate, tone.Color, tone.ProtectTones)
        End Function

        ''' <summary>Der magische Radierer: ein Klick nimmt die Flaeche weg, die der angeklickten
        ''' Farbe aehnelt, im Foto oder auf der markierten Bild-Ebene. Ein Schritt mit eigenem
        ''' Rueckgaengig; eine Auswahl begrenzt ihn wie jeden Pinsel.
        '''
        ''' Im Foto wird die Flaeche VOR dem Einreihen bestimmt: erst dann steht das Rechteck fest,
        ''' das sich aendert, und nur dieses geht in den Rueckgaengig-Schritt statt des ganzen
        ''' Bildes. Dazu darf keine Aenderung am Arbeitsbild mehr in der Warteschlange stehen, sonst
        ''' wuerde an einem Stand verglichen, der gleich nicht mehr gilt.</summary>
        Public Sub MagicEraseAt(xPercent As Double, yPercent As Double)
            If Not CanUsePixelTools Then Return
            Dim tone = CurrentToneBrushSettings()
            Dim label = LocalizationService.T("Magischer Radierer")

            Dim imageObject = FindStrokeTargetImageAnnotation()
            If imageObject IsNot Nothing Then
                ' Auf der Ebene laeuft der Klick ueber den Weg eines Zuges: zwei Punkte an derselben
                ' Stelle, der erste ist der Klick. Dort wird ohnehin das ganze Bild der Ebene neu
                ' geschrieben.
                Dim clickPoints As New List(Of Avalonia.Point) From {
                    New Avalonia.Point(xPercent, yPercent), New Avalonia.Point(xPercent + 0.001, yPercent)}
                If Not TryPaintStrokeIntoImageAnnotation(imageObject, clickPoints, isEraser:=False,
                                                         tone:=tone, toneLabel:=label) Then
                    StatusText = LocalizationService.T("Malen fehlgeschlagen")
                End If
                Return
            End If

            If Not _workingImage.IsInitialized Then Return
            If _pendingWorkingCommits > 0 Then
                StatusText = LocalizationService.T("Das Bild wird noch gerechnet, bitte gleich noch einmal klicken")
                Return
            End If
            Dim wip = DisplayPercentToWorkingImagePercent(xPercent, yPercent, allowOutside:=False)
            If Double.IsNaN(wip.X) OrElse Double.IsNaN(wip.Y) Then Return
            Dim seedX = CInt(Math.Floor(PercentXToPixels(wip.X)))
            Dim seedY = CInt(Math.Floor(PercentYToPixels(wip.Y)))
            Dim fullWidth = _workingImage.FullWidth
            seedX = Math.Max(0, Math.Min(fullWidth - 1, seedX))
            seedY = Math.Max(0, Math.Min(_workingImage.FullHeight - 1, seedY))

            Dim bounds As SKRectI = SKRectI.Empty
            Dim weights = _workingImage.WithFull(
                Function(full)
                    Dim found As SKRectI
                    Dim result = ImageProcessor.BuildMagicEraseWeights(full, seedX, seedY, tone.Tolerance, tone.Contiguous, found)
                    bounds = found
                    Return result
                End Function)
            If weights Is Nothing OrElse bounds.Width <= 0 OrElse bounds.Height <= 0 Then
                StatusText = LocalizationService.T("An dieser Stelle gibt es nichts zu radieren")
                Return
            End If

            Dim anyCoverage = False
            Dim selectionCoverage = BuildSelectionCoverageForWorkingRect(bounds, anyCoverage)
            If selectionCoverage IsNot Nothing AndAlso Not anyCoverage Then
                selectionCoverage.Dispose()
                Return
            End If
            If bounds.Width <= 0 OrElse bounds.Height <= 0 Then
                selectionCoverage?.Dispose()
                Return
            End If

            PushUndo(label)
            Dim undoEntry = _lastPushedUndoEntry
            _hasChanges = True
            RaiseResetButtonStateChanged()
            _previewTimer.Stop()
            _previewPending = False
            Dim rect = bounds

            EnqueueWorkingCommit(
                Function()
                    Try
                        Return _workingImage.CommitRegion(rect,
                            Sub(full)
                                Dim before As SKBitmap = Nothing
                                If selectionCoverage IsNot Nothing Then before = ImageProcessor.CopyRegion(full, rect)
                                Try
                                    If Not ImageProcessor.EraseByWeights(full, rect, weights, fullWidth, 0, 0) Then
                                        Throw New InvalidOperationException("Magischer Radierer konnte nicht angewendet werden")
                                    End If
                                    If selectionCoverage IsNot Nothing AndAlso
                                       Not ImageProcessor.RestoreOutsideCoverage(full, before, selectionCoverage, rect) Then
                                        Throw New InvalidOperationException("Auswahlgrenze konnte nicht angewendet werden")
                                    End If
                                Finally
                                    before?.Dispose()
                                End Try
                            End Sub, punchesAlpha:=True)
                    Finally
                        selectionCoverage?.Dispose()
                    End Try
                End Function,
                Sub(patch)
                    If patch Is Nothing Then
                        StatusText = LocalizationService.T("Malen fehlgeschlagen")
                        SchedulePreviewUpdate()
                        Return
                    End If
                    If undoEntry IsNot Nothing Then undoEntry.Patch = patch
                    ' Wie beim Hintergrund-Radierer: der Grund hinter dem Bild zeigt jetzt das Schachbrett.
                    Me.RaisePropertyChanged(NameOf(TransparencyBackgroundBrush))
                    SchedulePreviewUpdate()
                End Sub)
        End Sub

        ' ===================== Protokollpinsel (GitHub #106) =====================
        '
        ' Er malt das URSPRUNGSBILD zurueck: Retusche, Pinsel und Regler sind an der bemalten Stelle
        ' wieder weg. Gemalt wird nicht ins Arbeitsbild, sondern in die Maske einer Protokollebene
        ' (MaskedAdjustmentLayer.RestoresOriginal); der Renderer mischt darunter das Ursprungsbild
        ' durch dieselbe Geometrie ein (ImageProcessor, ApplyMaskedAdjustmentLayersCore). Damit
        ' bleibt alles zerstoerungsfrei, wird mit dem Rezept gespeichert und laesst sich als Ebene
        ' ausblenden, in der Deckkraft aendern oder loeschen.

        ''' <summary>Ein Zug des Protokollpinsels: seine Deckung (Groesse, Haerte, Deckkraft, Fluss
        ''' wie beim Bildpinsel, an der Auswahl begrenzt) geht als Maximum in die Maske der obersten
        ''' Protokollebene; fehlt eine, entsteht sie oben im Basisbild. EIN Rueckgaengig-Schritt, der
        ''' Maske und Ebene zusammen zuruecknimmt.</summary>
        Private Sub AddRestoreStroke(normalized As List(Of Avalonia.Point))
            Dim baseW = GetBaseWidth()
            Dim baseH = GetBaseHeight()
            If baseW <= 0 OrElse baseH <= 0 Then Return
            Dim mapped = MapStrokeToWorkingPixels(normalized)
            If mapped.Points.Count < 2 Then Return

            Dim options = BuildPixelPaintOptions(False)
            options.StrokeColor = "#FFFFFFFF"
            options.BrushPreset = "soft"
            options.ShadowEnabled = False
            options.GlowEnabled = False
            Dim dirtyFull As SKRectI
            Dim stroke = PixelEditLayer.CreateTransientStroke(mapped.Points, options, baseW, baseH, dirtyFull, mapped.Pressures)
            If stroke Is Nothing Then Return
            dirtyFull = ClampRectToBitmap(dirtyFull, baseW, baseH)
            If dirtyFull.Width <= 0 OrElse dirtyFull.Height <= 0 Then Return

            ' Zuerst die Auswahl: sie kann das Rechteck verkleinern, und die Deckung entsteht dann
            ' fuer das verbliebene.
            Dim anyCoverage = False
            Dim selectionCoverage = BuildSelectionCoverageForWorkingRect(dirtyFull, anyCoverage)
            Try
                If selectionCoverage IsNot Nothing AndAlso Not anyCoverage Then Return
                If dirtyFull.Width <= 0 OrElse dirtyFull.Height <= 0 Then Return
                Dim coverage = ImageProcessor.BuildStrokeCoverage(stroke.ToRenderAnnotation(), dirtyFull, baseW, baseH)
                If coverage Is Nothing Then Return
                If selectionCoverage IsNot Nothing Then MultiplyCoverageBySelection(coverage, dirtyFull, selectionCoverage)
                If Not coverage.Any(Function(b) b <> 0) Then Return

                PushUndo(LocalizationService.T("Protokollpinsel"))
                Dim mask = EnsureRestoreLayerMask(baseW, baseH)
                MergeCoverageIntoMask(mask, coverage, dirtyFull)
            Finally
                selectionCoverage?.Dispose()
            End Try

            _hasChanges = True
            RaiseResetButtonStateChanged()
            RebuildLayerRows()
            SchedulePreviewUpdate()
        End Sub

        ''' <summary>Die Maske der obersten Protokollebene im Basisbild; fehlt eine, entsteht sie OBEN
        ''' im Stapel, damit sie alles darunter zuruecknimmt. Nicht ueber PlaceNewCorrectionLayer: eine
        ''' markierte Ebene ist hier kein Ziel, das Ursprungsbild gehoert ins Basisbild.</summary>
        Private Function EnsureRestoreLayerMask(baseW As Integer, baseH As Integer) As ImageMask
            Dim layer = _maskedAdjustmentLayers.LastOrDefault(
                Function(l) l IsNot Nothing AndAlso l.RestoresOriginal AndAlso String.IsNullOrEmpty(l.StackAboveAnnotationId))
            If layer IsNot Nothing Then
                Dim existing = _imageMasks.FirstOrDefault(Function(m) m IsNot Nothing AndAlso String.Equals(m.Id, layer.MaskId, StringComparison.Ordinal))
                If existing IsNot Nothing Then Return existing
            End If
            Dim mask As New ImageMask With {
                .Name = GeneratedLayerNames.HistoryLayer,
                .SourceWidthPixels = baseW,
                .SourceHeightPixels = baseH
            }
            _imageMasks.Add(mask)
            If layer Is Nothing Then
                layer = New MaskedAdjustmentLayer With {
                    .Name = GeneratedLayerNames.HistoryLayer,
                    .MaskId = mask.Id,
                    .Adjustments = New ImageAdjustments(),
                    .IsMaskLayer = True,
                    .RestoresOriginal = True
                }
                _maskedAdjustmentLayers.Add(layer)
            Else
                layer.MaskId = mask.Id
            End If
            Return mask
        End Function

        ''' <summary>Die Deckung eines Strichs (ein Byte je Punkt in <paramref name="rect"/>) mit der
        ''' Auswahl im selben Rechteck multiplizieren.</summary>
        Private Shared Sub MultiplyCoverageBySelection(coverage As Byte(), rect As SKRectI, selection As SKBitmap)
            If selection Is Nothing OrElse selection.ColorType <> SKColorType.Alpha8 Then Return
            Dim w = rect.Width, h = rect.Height
            If selection.Width < w OrElse selection.Height < h Then Return
            Dim stride = selection.RowBytes
            Dim buffer = New Byte(stride * selection.Height - 1) {}
            Runtime.InteropServices.Marshal.Copy(selection.GetPixels(), buffer, 0, buffer.Length)
            For y = 0 To h - 1
                For x = 0 To w - 1
                    Dim i = y * w + x
                    coverage(i) = CByte((CInt(coverage(i)) * CInt(buffer(y * stride + x)) + 127) \ 255)
                Next
            Next
        End Sub

        ''' <summary>Die Deckung als MAXIMUM in die Maske: ein zweiter Zug ueber dieselbe Stelle
        ''' deckt nicht doppelt, ein schwacher Zug nimmt einen starken nicht zurueck. Das Rechteck der
        ''' Maske waechst auf die Vereinigung; das Raster wird ersetzt, nie veraendert (es ist
        ''' unveraenderlich und wird zwischen Rueckgaengig-Staenden geteilt).</summary>
        Private Shared Sub MergeCoverageIntoMask(mask As ImageMask, coverage As Byte(), rect As SKRectI)
            Dim oldRaster = mask.Raster
            Dim hasOld = oldRaster IsNot Nothing AndAlso mask.Right > mask.Left AndAlso mask.Bottom > mask.Top AndAlso
                         oldRaster.Width = mask.Right - mask.Left AndAlso oldRaster.Height = mask.Bottom - mask.Top
            Dim left = If(hasOld, Math.Min(mask.Left, rect.Left), rect.Left)
            Dim top = If(hasOld, Math.Min(mask.Top, rect.Top), rect.Top)
            Dim right = If(hasOld, Math.Max(mask.Right, rect.Right), rect.Right)
            Dim bottom = If(hasOld, Math.Max(mask.Bottom, rect.Bottom), rect.Bottom)
            Dim w = right - left, h = bottom - top
            Dim pixels = New Byte(w * h - 1) {}
            If hasOld Then
                Dim oldPixels = oldRaster.Pixels
                Dim ow = oldRaster.Width
                For y = 0 To oldRaster.Height - 1
                    Buffer.BlockCopy(oldPixels, y * ow, pixels, (y + mask.Top - top) * w + (mask.Left - left), ow)
                Next
            End If
            Dim rw = rect.Width
            For y = 0 To rect.Height - 1
                Dim target = (y + rect.Top - top) * w + (rect.Left - left)
                For x = 0 To rw - 1
                    Dim value = coverage(y * rw + x)
                    If value > pixels(target + x) Then pixels(target + x) = value
                Next
            Next
            mask.Left = left : mask.Top = top : mask.Right = right : mask.Bottom = bottom
            mask.Raster = New AlphaRaster(w, h, pixels)
        End Sub

    End Class

End Namespace
