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
        Private _replaceColor As String = "#FF3A7BD5"

        Private Shared ReadOnly ToneModes As String() = {"Dodge", "Burn", "Sponge", "ReplaceColor"}

        ''' <summary>Was ein Zug des Bildpinsels zum Umrechnen braucht, auf dem UI-Faden
        ''' eingefroren: der Hintergrund darf die Felder des Werkzeugs nicht lesen, und wer mitten im
        ''' Zug umstellt, meint den naechsten.</summary>
        Friend NotInheritable Class ToneBrushSettings
            Public Property Kind As String
            Public Property Range As String
            Public Property Saturate As Boolean
            Public Property Color As SKColor
        End Class

        Private Function CurrentToneBrushSettings() As ToneBrushSettings
            Dim replaceWith As SKColor
            If Not SKColor.TryParse(_replaceColor, replaceWith) Then replaceWith = New SKColor(58, 123, 213)
            Return New ToneBrushSettings With {.Kind = _toneMode, .Range = _toneRange,
                                               .Saturate = _spongeSaturate, .Color = replaceWith}
        End Function

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

        ''' <summary>Eine der vier Arten, die das Bild umrechnen (nicht der Radierer).</summary>
        Public ReadOnly Property IsToneBrushMode As Boolean
            Get
                Return _currentTool = EditorTool.Draw AndAlso _toneMode <> ""
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
                Dim text = value.ToString()
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
                Case Else : Return ""
            End Select
        End Function

        Private Function ToneModeIcon() As String
            Select Case _toneMode
                Case "Dodge" : Return "brightness-up.svg"
                Case "Burn" : Return "brightness-down.svg"
                Case "Sponge" : Return "droplet-half.svg"
                Case "ReplaceColor" : Return "replace.svg"
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
                                    If coverage Is Nothing OrElse
                                       Not ImageProcessor.ApplyToneBrush(full, dirtyFull, coverage, tone.Kind, tone.Range,
                                                                         tone.Saturate, tone.Color) Then
                                        Throw New InvalidOperationException("Bildpinsel konnte nicht angewendet werden")
                                    End If
                                    If selectionCoverage IsNot Nothing AndAlso
                                       Not ImageProcessor.RestoreOutsideCoverage(full, before, selectionCoverage, dirtyFull) Then
                                        Throw New InvalidOperationException("Auswahlgrenze konnte nicht angewendet werden")
                                    End If
                                Finally
                                    before?.Dispose()
                                End Try
                            End Sub)
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
                    SchedulePreviewUpdate()
                End Sub)
        End Sub

    End Class

End Namespace
