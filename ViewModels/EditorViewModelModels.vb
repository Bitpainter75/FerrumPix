Imports System
Imports System.Collections.Generic
Imports System.Collections.ObjectModel
Imports System.IO
Imports System.Linq
Imports System.Runtime.InteropServices
Imports System.Threading
Imports System.Threading.Tasks
Imports System.Windows.Input
Imports Avalonia.Media.Imaging
Imports Avalonia.Threading
Imports FerrumPix.Controls
Imports SkiaSharp
Imports ReactiveUI
Imports FerrumPix.Services
Imports FerrumPix.Models

Namespace ViewModels

    ''' <summary>Die modellgestuetzten Werkzeuge: Tiefen-Unschaerfe, Maske nach Tiefe und
    ''' Objektauswahl. Sie teilen dieselbe Bauform - das Modell laeuft EINMAL je Bild, sein Ergebnis
    ''' wird gemerkt, und die Regler rechnen daraus nur noch die Maske nach.
    '''
    ''' Vierte Scheibe der Dateiaufteilung (2026-08-04), Regeln wie in
    ''' <c>ViewModels/EditorViewModelMask.vb</c>. Hier war die Lambda-Falle besonders dicht: die
    ''' Modellwege rufen ihre Arbeit ueber mehrzeilige Sub- und Function-Ausdruecke auf, deren Ende
    ''' beim Zaehlen wie das Ende der Methode aussieht. Blockgrenzen kommen deshalb aus der
    ''' EINRUECKUNG der Klassenebene, nicht aus dem ersten passenden End.</summary>
    Partial Public Class EditorViewModel

        ' ── Bestand der Modelle ─────────────────────────────────────────────────

        ''' <summary>Meldet ALLE Eigenschaften neu, die davon abhaengen, ob eine Modelldatei
        ''' vorliegt. Angemeldet am Bestandsereignis des Modelldienstes, siehe dort.
        '''
        ''' Der Grund fuer die Sammelmethode: die Verfuegbarkeiten sind reine Nur-Lese-Sichten auf
        ''' den Dateibestand und haben kein eigenes Feld, das sich beim Setzen meldet. Wird ein
        ''' Modell im laufenden Programm geladen, aendert sich ihr Wert also, ohne dass es jemand
        ''' erfaehrt - die Regler blieben grau, bis das Programm neu startete. Sie stehen ALLE hier,
        ''' und nicht je Werkzeug verstreut: wer ein Modell hinzufuegt, uebersieht sonst genau die
        ''' eine Zeile, die den Knopf wieder aufweckt.
        '''
        ''' Die Hinweise gehoeren mit dazu. Sie kippen am selben Bestand von "dafuer fehlt eine
        ''' Modelldatei" auf die Beschreibung der Funktion; ohne Meldung stuende am nun bedienbaren
        ''' Regler weiter der Satz, die Datei fehle.</summary>
        Public Sub RefreshModelAvailability()
            ' Das Ereignis kommt aus dem Hintergrund - der Download laeuft dort.
            If Not Dispatcher.UIThread.CheckAccess() Then
                Dispatcher.UIThread.Post(Sub() RefreshModelAvailability())
                Return
            End If
            For Each name In {NameOf(IsBokehAvailable), NameOf(BokehHint),
                              NameOf(IsDepthMaskAvailable), NameOf(DepthMaskHint),
                              NameOf(IsSubjectMaskAvailable), NameOf(SubjectMaskHint),
                              NameOf(IsObjectRemovalAvailable), NameOf(CanRemoveObject), NameOf(RemoveObjectHint),
                              NameOf(CanDenoiseWithModel), NameOf(DenoiseWithModelHint),
                              NameOf(CanDenoiseFast), NameOf(DenoiseFastHint),
                              NameOf(CanDenoiseWithAnyModel)}
                Me.RaisePropertyChanged(name)
            Next
        End Sub

        ' ── Tiefen-Unschaerfe ───────────────────────────────────────────────────
        '
        ' Wird GEBACKEN, wie die Gitterverzerrung und aus demselben Grund: sie braucht die
        ' Tiefenkarte, und die ist eine Modellausgabe, kein Rezeptwert. Sie in die Kette zu haengen
        ' hiesse, bei jedem Render das Modell zu fragen. Bis zum Anwenden ist sie eine Vorschau.

        Private _bokehVon As Double = 70.0
        Private _bokehBis As Double = 100.0
        Private _bokehStaerke As Double = 40.0
        Private _bokehUebergang As Double = 25.0
        Private _bokehBlende As Integer = 0
        Private _bokehLichter As Double = 60.0

        Public ReadOnly Property IsBokehAvailable As Boolean
            Get
                Return DepthMapService.Available
            End Get
        End Property

        ''' <summary>Untere Grenze des scharfen Bandes. 0 ist am weitesten weg.
        '''
        ''' Zwei Grenzen statt einer Ebene mit Breite: eine echte Schaerfentiefe reicht nach hinten
        ''' weiter als nach vorn, und das laesst sich nur so einstellen.</summary>
        Public Property BokehFrom As Double
            Get
                Return _bokehVon
            End Get
            Set(value As Double)
                Dim v = Math.Max(0.0, Math.Min(100.0, value))
                If Math.Abs(_bokehVon - v) < 0.0001 Then Return
                _bokehVon = v
                ' Die Grenzen schieben einander, statt sich zu kreuzen - eine untere Grenze ueber
                ' der oberen ist kein Band, sondern ein Zustand, den man nicht meinen kann.
                If _bokehVon > _bokehBis Then
                    _bokehBis = _bokehVon
                    Me.RaisePropertyChanged(NameOf(BokehTo))
                End If
                Me.RaisePropertyChanged(NameOf(BokehFrom))
                ScheduleBokehPreview()
            End Set
        End Property

        ''' <summary>Obere Grenze des scharfen Bandes. 100 ist am naechsten.</summary>
        Public Property BokehTo As Double
            Get
                Return _bokehBis
            End Get
            Set(value As Double)
                Dim v = Math.Max(0.0, Math.Min(100.0, value))
                If Math.Abs(_bokehBis - v) < 0.0001 Then Return
                _bokehBis = v
                If _bokehBis < _bokehVon Then
                    _bokehVon = _bokehBis
                    Me.RaisePropertyChanged(NameOf(BokehFrom))
                End If
                Me.RaisePropertyChanged(NameOf(BokehTo))
                ScheduleBokehPreview()
            End Set
        End Property

        ''' <summary>Wie stark die Unschaerfe am fernsten Punkt wird.</summary>
        Public Property BokehStrength As Double
            Get
                Return _bokehStaerke
            End Get
            Set(value As Double)
                Dim v = Math.Max(0.0, Math.Min(100.0, value))
                If Math.Abs(_bokehStaerke - v) < 0.0001 Then Return
                _bokehStaerke = v
                Me.RaisePropertyChanged(NameOf(BokehStrength))
                Me.RaisePropertyChanged(NameOf(HasBokehChanges))
                ScheduleBokehPreview()
            End Set
        End Property

        ''' <summary>Wie breit die Schaerfeebene ist: klein ergibt einen schmalen scharfen Bereich,
        ''' gross einen breiten.</summary>
        Public Property BokehTransition As Double
            Get
                Return _bokehUebergang
            End Get
            Set(value As Double)
                Dim v = Math.Max(1.0, Math.Min(100.0, value))
                If Math.Abs(_bokehUebergang - v) < 0.0001 Then Return
                _bokehUebergang = v
                Me.RaisePropertyChanged(NameOf(BokehTransition))
                ScheduleBokehPreview()
            End Set
        End Property

        ''' <summary>Die Form der Blende: 0 rund, sonst die Zahl der Lamellen. Sie bestimmt, wie ein
        ''' Lichtpunkt im unscharfen Bereich aussieht - rund oder als Vieleck.</summary>
        Public Property BokehAperture As Integer
            Get
                Return _bokehBlende
            End Get
            Set(value As Integer)
                ' Zwischen 1 und 2 gibt es keine Form: entweder rund (0) oder mindestens ein Dreieck.
                Dim v = Math.Max(0, Math.Min(9, value))
                If v = 1 OrElse v = 2 Then v = 0
                If v = _bokehBlende Then Return
                _bokehBlende = v
                Me.RaisePropertyChanged(NameOf(BokehAperture))
                For Each n In {NameOf(IsApertureRound), NameOf(IsApertureFive),
                               NameOf(IsApertureSix), NameOf(IsApertureSeven)}
                    Me.RaisePropertyChanged(n)
                Next
                ScheduleBokehPreview()
            End Set
        End Property

        Public ReadOnly Property IsApertureRound As Boolean
            Get
                Return _bokehBlende <= 0
            End Get
        End Property

        Public ReadOnly Property IsApertureFive As Boolean
            Get
                Return _bokehBlende = 5
            End Get
        End Property

        Public ReadOnly Property IsApertureSix As Boolean
            Get
                Return _bokehBlende = 6
            End Get
        End Property

        Public ReadOnly Property IsApertureSeven As Boolean
            Get
                Return _bokehBlende = 7
            End Get
        End Property

        ''' <summary>Wie stark Lichtpunkte als leuchtende Scheiben erhalten bleiben, statt sich
        ''' zu einem matten Fleck zu mitteln.</summary>
        Public Property BokehHighlights As Double
            Get
                Return _bokehLichter
            End Get
            Set(value As Double)
                Dim v = Math.Max(0.0, Math.Min(100.0, value))
                If Math.Abs(_bokehLichter - v) < 0.0001 Then Return
                _bokehLichter = v
                Me.RaisePropertyChanged(NameOf(BokehHighlights))
                ScheduleBokehPreview()
            End Set
        End Property

        Public ReadOnly Property HasBokehChanges As Boolean
            Get
                Return _bokehStaerke > 0.01
            End Get
        End Property

        ''' <summary>Die Gruppe auf ihren Ausgangsstand zurueck. Das Backen kann das nicht
        ''' zuruecknehmen - dafuer ist der Schritt zurueck da.</summary>
        Public Sub ResetBokeh()
            _bokehVon = 70.0
            _bokehBis = 100.0
            _bokehStaerke = 0.0
            _bokehUebergang = 25.0
            _bokehBlende = 0
            _bokehLichter = 60.0
            DisposeBokehPreview()
            For Each n In {NameOf(BokehFrom), NameOf(BokehTo), NameOf(BokehStrength), NameOf(BokehTransition),
                           NameOf(BokehAperture), NameOf(BokehHighlights), NameOf(HasBokehChanges),
                           NameOf(IsApertureRound), NameOf(IsApertureFive), NameOf(IsApertureSix),
                           NameOf(IsApertureSeven)}
                Me.RaisePropertyChanged(n)
            Next
        End Sub

        ' ── Live-Vorschau der Tiefen-Unschaerfe ─────────────────────────────────
        '
        ' Gleiche Bauform wie bei der Gitterverzerrung und aus demselben Grund: die Wirkung wird erst
        ' beim Anwenden in die Pixel gerechnet, aber ohne Vorschau stellt man drei Regler blind ein.
        '
        ' Gerechnet wird auf einer VERKLEINERTEN Kopie des Anzeigebildes ohne Objekte. Die Unschaerfe
        ' faltet je Stufe ueber die ganze Flaeche; in voller Groesse waere das je Reglerschritt zu
        ' teuer, und fuer die Beurteilung von Lage und Staerke reicht die kleine Fassung. Die Anzeige
        ' zieht sie ohnehin auf den Bildbereich.

        ''' <summary>Laengste Kante der Vorschaukopie.</summary>
        Private Const BokehPreviewEdge As Integer = 900

        Private _bokehVorschauBasis As SKBitmap
        Private _bokehVorschauSchluessel As String = ""
        Private _bokehVorschauLauf As Integer = 0

        Public Sub DisposeBokehPreview()
            If String.Equals(_vorschauQuelle, "Bokeh", StringComparison.Ordinal) Then
                ToolPreviewImage = Nothing
                _vorschauQuelle = ""
            End If
            _bokehVorschauBasis?.Dispose()
            _bokehVorschauBasis = Nothing
            _bokehVorschauSchluessel = ""
            ' Ein noch laufender Anlauf darf sein Ergebnis nicht mehr einhaengen.
            _bokehVorschauLauf += 1
        End Sub

        ''' <summary>Nach einer Reglerbewegung eine neue Vorschau anstossen.
        '''
        ''' Mit Wartezeit: waehrend man am Regler zieht, kommen Dutzende Aenderungen, und jede eine
        ''' Faltung ueber das ganze Bild zu starten hiesse, dass die Anzeige immer der Maus
        ''' hinterherhinkt. Es zaehlt nur der zuletzt angestossene Lauf.</summary>
        Private Sub ScheduleBokehPreview()
            If Not IsBokehAvailable Then Return
            _bokehVorschauLauf += 1
            Dim pass = _bokehVorschauLauf
            Dim ignoriert = RedrawBokehPreview(pass)
        End Sub

        Private Async Function RedrawBokehPreview(pass As Integer) As Task
            Await Task.Delay(220)
            If pass <> _bokehVorschauLauf Then Return
            If Not IsBokehAvailable OrElse String.IsNullOrWhiteSpace(_currentImagePath) Then Return

            If Not HasBokehChanges Then
                DisposeBokehPreview()
                Return
            End If

            Await _depthGate.WaitAsync()
            Try
                If pass <> _bokehVorschauLauf Then Return
                If Not Await PrepareBokehBase() Then Return
                If pass <> _bokehVorschauLauf Then Return

                Dim basis = _bokehVorschauBasis
                Dim map = _depthMap
                If basis Is Nothing OrElse map Is Nothing Then Return
                Dim from = _bokehVon, bis = _bokehBis
                Dim strength = _bokehStaerke, uebergang = _bokehUebergang
                Dim corners = _bokehBlende, lichter = _bokehLichter

                SetPreviewBusy(True)
                Dim data As Byte()
                Try
                    data = Await Task.Run(
                    Function() As Byte()
                        Using unscharf = DepthMapService.DepthBlur(basis, map, from, bis,
                                                                            strength, uebergang, corners, lichter)
                            If unscharf Is Nothing Then Return Nothing
                            Using image = SKImage.FromBitmap(unscharf)
                                Using roh = image.Encode(SKEncodedImageFormat.Png, 90)
                                    Return roh.ToArray()
                                End Using
                            End Using
                        End Using
                    End Function)
                Finally
                    SetPreviewBusy(False)
                End Try

                If data Is Nothing OrElse pass <> _bokehVorschauLauf Then Return
                Using strom = New IO.MemoryStream(data)
                    ToolPreviewImage = New Bitmap(strom)
                    _vorschauQuelle = "Bokeh"
                End Using
            Catch ex As Exception
                DiagnosticLogService.LogAlways("BokehVorschau", ex.Message)
                DisposeBokehPreview()
            Finally
                _depthGate.Release()
            End Try
        End Function

        ''' <summary>Die verkleinerte Kopie und die Tiefenkarte fuer den aktuellen Bildstand.
        ''' Beide haengen am selben Schluessel: aendert sich das Rezept, gelten beide nicht mehr.
        ''' Der Aufrufer haelt <c>_depthGate</c>.</summary>
        Private Async Function PrepareBokehBase() As Task(Of Boolean)
            Dim size = GetAnnotationDisplayPixelSize()
            If size.Width <= 0 OrElse size.Height <= 0 Then Return False
            Dim rezept = RecipeWithoutObjects()
            If rezept Is Nothing Then Return False
            Dim key = String.Join("|", _currentImagePath, size.Width, size.Height,
                                         ImageProcessor.ComputeBaseKey(rezept))
            If _bokehVorschauBasis IsNot Nothing AndAlso _depthMap IsNot Nothing AndAlso
               String.Equals(_bokehVorschauSchluessel, key, StringComparison.Ordinal) AndAlso
               String.Equals(_depthKey, key, StringComparison.Ordinal) Then
                Return True
            End If

            _depthRunning = True
            Me.RaisePropertyChanged(NameOf(IsDepthMaskRunning))
                    RefreshBusyState()
            StatusText = LocalizationService.T("Tiefe wird berechnet…")
                SetBusyReason(LocalizationService.T("Tiefe wird berechnet"))
            Dim sourcePath = RenderSourcePath
            Dim workingImage = CloneWorkingFullForRender()
            Dim needsMap = _depthMap Is Nothing OrElse
                               Not String.Equals(_depthKey, key, StringComparison.Ordinal)
            Dim paar As Tuple(Of SKBitmap, SKBitmap) = Nothing
            Try
                paar = Await Task.Run(
                    Function()
                        Using fertig = ImageProcessor.RenderDisplayImage(sourcePath, rezept, workingImage)
                            If fertig Is Nothing Then Return Nothing
                            ' EIN Render fuer beides: die Tiefenkarte und die Vorschaukopie kommen aus
                            ' demselben Bild, sonst passten sie im Zweifel nicht zusammen.
                            Dim k As SKBitmap = Nothing
                            If needsMap Then k = DepthMapService.Compute(fertig)
                            Return Tuple.Create(ScaledDownCopy(fertig, BokehPreviewEdge), k)
                        End Using
                    End Function)
            Finally
                _depthRunning = False
                Me.RaisePropertyChanged(NameOf(IsDepthMaskRunning))
                    RefreshBusyState()
            End Try

            If paar Is Nothing OrElse paar.Item1 Is Nothing Then
                paar?.Item2?.Dispose()
                StatusText = LocalizationService.T("Tiefe konnte nicht berechnet werden")
                Return False
            End If
            If needsMap Then
                If paar.Item2 Is Nothing Then
                    paar.Item1.Dispose()
                    StatusText = LocalizationService.T("Tiefe konnte nicht berechnet werden")
                    Return False
                End If
                _depthMap?.Dispose()
                _depthMap = paar.Item2
                _depthKey = key
            End If
            _bokehVorschauBasis?.Dispose()
            _bokehVorschauBasis = paar.Item1
            _bokehVorschauSchluessel = key
            StatusText = ""
            Return True
        End Function

        ''' <summary>Das aktuelle Rezept OHNE die eingefuegten Ebenen.
        '''
        ''' Fuer alles, was mit Tiefe zu tun hat, ist das die richtige Vorlage. Ein aufgeklebtes
        ''' Objekt hat keine Entfernung - das Modell wuerde ihm eine andichten, und die Unschaerfe
        ''' liefe darum herum, statt es zu treffen. In der Vorschau kommt hinzu: die Objekte werden
        ''' beim Anwenden ohnehin nicht mitgerechnet, und scharf ueber einem verwischten Hintergrund
        ''' zeigten sie etwas, das nachher anders aussieht.</summary>
        Private Function RecipeWithoutObjects() As ImageAdjustments
            Dim rezept = GetCurrentAdjustments()
            If rezept Is Nothing Then Return Nothing
            rezept.Annotations = New List(Of ImageAnnotation)()
            Return rezept
        End Function

        ''' <summary>Eine Kopie, deren laengste Kante hoechstens <paramref name="edge"/> misst.
        ''' Ist das Bild schon kleiner, wird es nur kopiert, nicht hochgezogen.</summary>
        Private Shared Function ScaledDownCopy(source As SKBitmap, edge As Integer) As SKBitmap
            If source Is Nothing OrElse source.Width <= 0 OrElse source.Height <= 0 Then Return Nothing
            Dim f = Math.Min(1.0, edge / CDbl(Math.Max(source.Width, source.Height)))
            Dim w = Math.Max(1, CInt(Math.Round(source.Width * f)))
            Dim h = Math.Max(1, CInt(Math.Round(source.Height * f)))
            Dim target = New SKBitmap(w, h, source.ColorType, source.AlphaType)
            If Not source.ScalePixels(target, New SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear)) Then
                target.Dispose()
                Return Nothing
            End If
            Return target
        End Function

        ''' <summary>Die Tiefen-Unschaerfe ins Arbeitsbild backen. Ab hier ist sie Teil der Pixel.</summary>
        Public Async Function ApplyBokeh() As Task
            If Not DepthMapService.Available OrElse Not HasBokehChanges Then Return
            If _workingImage Is Nothing OrElse Not _workingImage.IsInitialized Then Return

            Await _depthGate.WaitAsync()
            Try
                _depthRunning = True
                Me.RaisePropertyChanged(NameOf(IsDepthMaskRunning))
                    RefreshBusyState()
                StatusText = LocalizationService.T("Tiefe wird berechnet…")
                SetBusyReason(LocalizationService.T("Tiefe wird berechnet"))
                Dim sourcePath = RenderSourcePath
                Dim rezept = RecipeWithoutObjects()
                Dim workingImage = CloneWorkingFullForRender()
                Dim map As SKBitmap = Nothing
                Try
                    map = Await Task.Run(
                        Function()
                            Using fertig = ImageProcessor.RenderDisplayImage(sourcePath, rezept, workingImage)
                                If fertig Is Nothing Then Return Nothing
                                Return DepthMapService.Compute(fertig)
                            End Using
                        End Function)
                Finally
                    _depthRunning = False
                    Me.RaisePropertyChanged(NameOf(IsDepthMaskRunning))
                    RefreshBusyState()
                End Try
                If map Is Nothing Then
                    StatusText = LocalizationService.T("Tiefe konnte nicht berechnet werden")
                    Return
                End If

                Dim from = _bokehVon, bis = _bokehBis
                Dim strength = _bokehStaerke, uebergang = _bokehUebergang
                Dim corners = _bokehBlende, lichter = _bokehLichter
                PushUndo()
                ' Der Schritt zurueck merkt sich nur das Rezept - der Flicken traegt die PIXEL.
                Dim undoItem = _lastPushedUndoEntry
                StatusText = LocalizationService.T("Unschärfe wird angewendet…")
                SetBusyReason(LocalizationService.T("Unschärfe wird angewendet"))
                EnqueueWorkingCommit(
                    Function()
                        Return _workingImage.CommitRegion(New SKRectI(0, 0, _workingImage.FullWidth, _workingImage.FullHeight),
                            Sub(full)
                                Using unscharf = DepthMapService.DepthBlur(full, map, from, bis,
                                                                                    strength, uebergang, corners, lichter)
                                    If unscharf Is Nothing Then Return
                                    Using canvas = New SKCanvas(full)
                                        canvas.Clear(SKColors.Transparent)
                                        Using paint = New SKPaint With {.BlendMode = SKBlendMode.Src}
                                            canvas.DrawBitmap(unscharf, 0, 0, paint)
                                        End Using
                                    End Using
                                End Using
                            End Sub)
                    End Function,
                    Sub(patch)
                        map.Dispose()
                        If patch Is Nothing Then
                            StatusText = LocalizationService.T("Unschärfe fehlgeschlagen")
                            Return
                        End If
                        If undoItem IsNot Nothing Then undoItem.Patch = patch
                        BokehStrength = 0
                        DisposeBokehPreview()
                        StatusText = LocalizationService.T("Unschärfe angewendet")
                        SchedulePreviewUpdate()
                    End Sub)
            Finally
                _depthGate.Release()
            End Try
        End Function
        ' ── Maske nach Tiefe ────────────────────────────────────────────────────
        '
        ' Dieselbe Bauform wie bei der Objektauswahl: das Modell laeuft EINMAL je Bild, die
        ' Tiefenkarte wird gemerkt, und die Regler rechnen nur noch die Maske daraus neu. Der Zusatz
        ' ist, dass es hier gar keinen Klick gibt - ein Bild rein, eine Karte raus.

        Private _depthMap As SKBitmap
        Private _depthKey As String = ""
        Private _depthFrom As Double = 60.0
        Private _depthTo As Double = 100.0
        Private _depthFeather As Double = 8.0
        Private _depthRunning As Boolean = False
        Private ReadOnly _depthGate As New SemaphoreSlim(1, 1)

        Public ReadOnly Property IsDepthMaskAvailable As Boolean
            Get
                Return DepthMapService.Available
            End Get
        End Property

        Public ReadOnly Property IsMaskDepthMode As Boolean
            Get
                Return String.Equals(_maskMode, "Tiefe", StringComparison.Ordinal)
            End Get
        End Property

        Public ReadOnly Property IsDepthMaskRunning As Boolean
            Get
                Return _depthRunning
            End Get
        End Property

        ''' <summary>Der nahe Rand des gewaehlten Bereichs, 0 bis 100. 100 ist am naechsten.</summary>
        Public Property DepthFrom As Double
            Get
                Return _depthFrom
            End Get
            Set(value As Double)
                Dim v = Math.Max(0.0, Math.Min(100.0, value))
                If Math.Abs(_depthFrom - v) < 0.0001 Then Return
                _depthFrom = v
                Me.RaisePropertyChanged(NameOf(DepthFrom))
                Dim ignoriert = RedrawDepthMask()
            End Set
        End Property

        Public Property DepthTo As Double
            Get
                Return _depthTo
            End Get
            Set(value As Double)
                Dim v = Math.Max(0.0, Math.Min(100.0, value))
                If Math.Abs(_depthTo - v) < 0.0001 Then Return
                _depthTo = v
                Me.RaisePropertyChanged(NameOf(DepthTo))
                Dim ignoriert = RedrawDepthMask()
            End Set
        End Property

        ''' <summary>Wie weit die Maske an beiden Grenzen auslaeuft. Ohne das entstuende an jeder
        ''' Tiefenstufe eine sichtbare Kante quer durchs Bild - eine Tiefenkarte ist stetig, eine
        ''' harte Schwelle darin sieht man immer.</summary>
        Public Property DepthFeather As Double
            Get
                Return _depthFeather
            End Get
            Set(value As Double)
                Dim v = Math.Max(0.0, Math.Min(50.0, value))
                If Math.Abs(_depthFeather - v) < 0.0001 Then Return
                _depthFeather = v
                Me.RaisePropertyChanged(NameOf(DepthFeather))
                Dim ignoriert = RedrawDepthMask()
            End Set
        End Property

        Public Sub ForgetDepthMap()
            _depthMap?.Dispose()
            _depthMap = Nothing
            _depthKey = ""
        End Sub

        ''' <summary>Die Maske aus dem gewaehlten Tiefenbereich. Rechnet die Tiefenkarte, falls sie
        ''' fuer diesen Bildstand noch fehlt.</summary>
        Public Async Function RedrawDepthMask() As Task
            If Not DepthMapService.Available Then Return
            If String.IsNullOrWhiteSpace(_currentImagePath) Then Return

            Await _depthGate.WaitAsync()
            Try
                Dim size = GetAnnotationDisplayPixelSize()
                If size.Width <= 0 OrElse size.Height <= 0 Then Return
                ' DASSELBE Rezept wie die Vorschau: ohne Objekte. Zwei verschiedene Vorlagen unter
                ' einem Schluessel waeren zwei verschiedene Tiefenkarten, je nachdem wer zuerst
                ' gerechnet hat.
                Dim rezept = RecipeWithoutObjects()
                If rezept Is Nothing Then Return
                Dim key = String.Join("|", _currentImagePath, size.Width, size.Height,
                                             ImageProcessor.ComputeBaseKey(rezept))
                If _depthMap Is Nothing OrElse Not String.Equals(_depthKey, key, StringComparison.Ordinal) Then
                    _depthRunning = True
                    Me.RaisePropertyChanged(NameOf(IsDepthMaskRunning))
                    RefreshBusyState()
                    StatusText = LocalizationService.T("Tiefe wird berechnet…")
                SetBusyReason(LocalizationService.T("Tiefe wird berechnet"))
                    Dim sourcePath = RenderSourcePath
                    Dim workingImage = CloneWorkingFullForRender()
                    Dim neu As SKBitmap = Nothing
                    Try
                        neu = Await Task.Run(
                            Function()
                                Using fertig = ImageProcessor.RenderDisplayImage(sourcePath, rezept, workingImage)
                                    If fertig Is Nothing Then Return Nothing
                                    Return DepthMapService.Compute(fertig)
                                End Using
                            End Function)
                    Finally
                        _depthRunning = False
                        Me.RaisePropertyChanged(NameOf(IsDepthMaskRunning))
                    RefreshBusyState()
                    End Try
                    If neu Is Nothing Then
                        StatusText = LocalizationService.T("Tiefe konnte nicht berechnet werden")
                        Return
                    End If
                    _depthMap?.Dispose()
                    _depthMap = neu
                    _depthKey = key
                End If

                Dim map = _depthMap
                Dim from = _depthFrom, bis = _depthTo, weich = _depthFeather
                Dim editingMaskId = _editingLayerMaskId
                Dim mask = Await Task.Run(Function() DepthMapService.MaskFromDepth(map, from, bis, weich))
                If mask Is Nothing Then Return
                Using mask
                    Dim rect = MaskRect(mask)
                    If rect.Width <= 0 OrElse rect.Height <= 0 Then
                        StatusText = LocalizationService.T("In diesem Tiefenbereich liegt nichts")
                        Return
                    End If
                    Using ausschnitt = ExtractMaskRegion(mask, rect)
                        If ausschnitt Is Nothing Then Return
                        _pendingRangeKind = "Depth" : _pendingRangeSampleX = 0 : _pendingRangeSampleY = 0
                        ApplySelectionCandidate(ausschnitt, rect, "MagicWand", Nothing, Nothing,
                                                isMask:=True, forceNew:=True)
                        If editingMaskId <> "" Then _editingLayerMaskId = editingMaskId
                    End Using
                    PersistRangeMaskIfEditing()
                    StatusText = LocalizationService.T("Maske nach Tiefe gesetzt")
                End Using
            Finally
                _depthGate.Release()
            End Try
        End Function

        ''' <summary>Kantenschaerfe der Objektmaske, 0 bis 100. Aendert nur die Umrechnung der
        ''' Modellausgabe in Deckung, nicht die Erkennung - das Modell wird deshalb NICHT erneut
        ''' gefragt, die Maske entsteht in Millisekunden neu.</summary>
        Public Property SubjectMaskEdge As Double
            Get
                Return _motivKante
            End Get
            Set(value As Double)
                Dim v = Math.Max(0.0, Math.Min(100.0, value))
                If Math.Abs(_motivKante - v) < 0.0001 Then Return
                _motivKante = v
                Me.RaisePropertyChanged(NameOf(SubjectMaskEdge))
                Dim ignoriert = RedrawSubjectMask()
            End Set
        End Property

        ''' <summary>Umfang der Objektmaske, -100 bis 100: waechst oder schrumpft sie um die
        ''' Kante herum.</summary>
        Public Property SubjectMaskExtent As Double
            Get
                Return _motivUmfang
            End Get
            Set(value As Double)
                Dim v = Math.Max(-100.0, Math.Min(100.0, value))
                If Math.Abs(_motivUmfang - v) < 0.0001 Then Return
                _motivUmfang = v
                Me.RaisePropertyChanged(NameOf(SubjectMaskExtent))
                Dim ignoriert = RedrawSubjectMask()
            End Set
        End Property

        Private _motivKante As Double = 50.0
        Private _motivUmfang As Double = 25.0
        ' Die MITTLERE Koernung als Vorgabe: die grobste faellt bei einem freistehenden Motiv
        ' gern auf das ganze Bild zusammen, die feinste greift nur einen Teil heraus. Die mittlere
        ' ist das, was man mit "dieses Objekt" meistens meint.
        Private _motivKoernung As Integer = 1

        ''' <summary>Welche Koernung gewaehlt ist: 0 fein, 1 mittel, 2 grob. Ein Klick ist
        ''' mehrdeutig - meint man die Jacke, die Person oder die Gruppe? Das Modell beantwortet
        ''' alle drei auf einmal, und die Wahl kostet keinen neuen Klick und keine Modellabfrage.</summary>
        Public Property SubjectMaskGrain As Integer
            Get
                Return _motivKoernung
            End Get
            Set(value As Integer)
                Dim v = Math.Max(0, Math.Min(2, value))
                If _motivKoernung = v Then Return
                _motivKoernung = v
                For Each n In {NameOf(SubjectMaskGrain), NameOf(IsGrainFine),
                               NameOf(IsGrainMedium), NameOf(IsGrainCoarse)}
                    Me.RaisePropertyChanged(n)
                Next
                Dim ignoriert = RedrawSubjectMask()
            End Set
        End Property

        Public ReadOnly Property IsGrainFine As Boolean
            Get
                Return _motivKoernung = 0
            End Get
        End Property

        Public ReadOnly Property IsGrainMedium As Boolean
            Get
                Return _motivKoernung = 1
            End Get
        End Property

        Public ReadOnly Property IsGrainCoarse As Boolean
            Get
                Return _motivKoernung = 2
            End Get
        End Property

        ''' <summary>Die Maske aus den bereits gesammelten Klicks neu zeichnen. Ohne das Modell
        ''' erneut zu fragen: die Einbettung und die Punkte stehen, nur die Umrechnung aendert sich.</summary>
        Private Async Function RedrawSubjectMask() As Task
            If _motivEinbettung Is Nothing OrElse _motivPunkte.Count = 0 Then Return
            Await _motivTor.WaitAsync()
            Try
                Dim einbettung = _motivEinbettung
                Dim points = _motivPunkte.ToList()
                Dim edge = _motivKante, umfang = _motivUmfang, koernung = _motivKoernung
                Dim mask = Await Task.Run(Function() SubjectMaskService.MaskFor(einbettung, points, edge, umfang, koernung))
                If mask Is Nothing Then Return
                Using mask
                    Dim rect = MaskRect(mask)
                    If rect.Width <= 0 OrElse rect.Height <= 0 Then Return
                    Using ausschnitt = ExtractMaskRegion(mask, rect)
                        If ausschnitt Is Nothing Then Return
                        ApplySelectionCandidate(ausschnitt, rect, "MagicWand", Nothing, Nothing,
                                                isMask:=True, forceNew:=True)
                    End Using
                End Using
            Finally
                _motivTor.Release()
            End Try
        End Function

        ''' <summary>Ein Klick ins Bild: die Maske des getroffenen Objekts.
        '''
        ''' <paramref name="dazu"/> False heisst "gehoert ausdruecklich nicht dazu" - damit schneidet
        ''' man eine zu gross geratene Maske zurecht, ohne von vorn anzufangen. Die Punkte sammeln
        ''' sich, bis der Modus verlassen oder die Auswahl geleert wird.</summary>
        Public Async Function SetSelectionSubjectMask(xPercent As Double, yPercent As Double,
                                                     dazu As Boolean) As Task
            If Not SubjectMaskService.Available Then Return
            If String.IsNullOrWhiteSpace(_currentImagePath) Then Return

            Await _motivTor.WaitAsync()
            Try
                Dim size = GetAnnotationDisplayPixelSize()
                Dim bw = size.Width, bh = size.Height
                If bw <= 0 OrElse bh <= 0 Then Return

                Dim rezept = GetCurrentAdjustments()
                Dim key = String.Join("|", _currentImagePath, bw, bh,
                                             ImageProcessor.ComputeBaseKey(rezept))
                If _motivEinbettung Is Nothing OrElse Not String.Equals(_motivSchluessel, key, StringComparison.Ordinal) Then
                    _motivPunkte.Clear()
                    _subjectRunning = True
                    Me.RaisePropertyChanged(NameOf(IsSubjectMaskRunning))
                    RefreshBusyState()
                    StatusText = LocalizationService.T("Bild wird für die Objektauswahl gelesen…")
                SetBusyReason(LocalizationService.T("Bild wird gelesen"))
                    Dim sourcePath = RenderSourcePath
                    Dim workingImage = CloneWorkingFullForRender()
                    Try
                        _motivEinbettung = Await Task.Run(
                            Function()
                                Using fertig = ImageProcessor.RenderDisplayImage(sourcePath, rezept, workingImage)
                                    If fertig Is Nothing Then Return Nothing
                                    Return SubjectMaskService.Kodiere(fertig)
                                End Using
                            End Function)
                    Finally
                        _subjectRunning = False
                        Me.RaisePropertyChanged(NameOf(IsSubjectMaskRunning))
                    RefreshBusyState()
                    End Try
                    If _motivEinbettung Is Nothing Then
                        StatusText = LocalizationService.T("Objektauswahl nicht möglich")
                        Return
                    End If
                    _motivSchluessel = key
                End If

                ' Der Modus entscheidet, was ein Klick bedeutet - dieselben drei Knoepfe wie beim
                ' Maskenpinsel. "Neu" faengt bei jedem Klick ein neues Objekt an, "Hinzufuegen"
                ' erweitert das begonnene, "Abziehen" nimmt eine Stelle wieder weg. Die Alt-Taste
                ' bleibt die Abkuerzung fuer Abziehen, ohne den Modus zu wechseln.
                If String.Equals(_selectionCombineMode, "New", StringComparison.Ordinal) AndAlso dazu Then
                    _motivPunkte.Clear()
                End If
                Dim gehoertDazu = dazu AndAlso Not String.Equals(_selectionCombineMode, "Subtract", StringComparison.Ordinal)
                If _motivPunkte.Count = 0 AndAlso Not gehoertDazu Then
                    ' Ein Abzugspunkt ohne etwas, wovon man abziehen koennte, ergibt nichts.
                    StatusText = LocalizationService.T("An dieser Stelle wurde kein Objekt gefunden")
                    Return
                End If
                _motivPunkte.Add(New SubjectMaskService.Point(
                    bw * xPercent / 100.0, bh * yPercent / 100.0, gehoertDazu))

                Dim einbettung = _motivEinbettung
                Dim points = _motivPunkte.ToList()
                Dim edge = _motivKante, umfang = _motivUmfang, koernung = _motivKoernung
                Dim mask = Await Task.Run(Function() SubjectMaskService.MaskFor(einbettung, points, edge, umfang, koernung))
                If mask Is Nothing Then
                    StatusText = LocalizationService.T("Objektauswahl nicht möglich")
                    Return
                End If
                Using mask
                    Dim rect = MaskRect(mask)
                    If rect.Width <= 0 OrElse rect.Height <= 0 Then
                        StatusText = LocalizationService.T("An dieser Stelle wurde kein Objekt gefunden")
                        _motivPunkte.RemoveAt(_motivPunkte.Count - 1)
                        Return
                    End If
                    ' Die Auswahlmaschinerie erwartet das Raster in der Groesse des RECHTECKS, nicht
                    ' des Bildes - so liefert es auch der Maskenpinsel. Ein bildgrosses Raster mit
                    ' einem kleineren Rechteck daneben wird als dessen Inhalt gelesen und sitzt dann
                    ' skaliert und versetzt.
                    Using ausschnitt = ExtractMaskRegion(mask, rect)
                        If ausschnitt Is Nothing Then Return
                        PushUndo()
                        ApplySelectionCandidate(ausschnitt, rect, "MagicWand", Nothing, Nothing,
                                                isMask:=True, forceNew:=True)
                    End Using
                    StatusText = LocalizationService.T("Objekt ausgewählt")
                End Using
            Finally
                _motivTor.Release()
            End Try
        End Function

        ' ── Bereichsmasken ──────────────────────────────────────────────────────
        '
        ' Farbe und Luminanz laufen absichtlich durch dieselbe Auswahl-Pipeline wie Pinsel,
        ' Tiefe und Objekt. Dadurch lassen sie sich sofort addieren, abziehen, schneiden und mit
        ' dem Pinsel nacharbeiten, bevor sie als lokale Anpassungsebene gespeichert werden.
        Private ReadOnly _rangeMaskGate As New SemaphoreSlim(1, 1)
        Private _colorRangeTolerance As Double = 18.0
        Private _colorRangeFeather As Double = 14.0
        Private _colorRangeContiguous As Boolean = False
        Private _luminanceRangeFrom As Double = 0.0
        Private _luminanceRangeTo As Double = 100.0
        Private _luminanceRangeFeather As Double = 12.0
        Private _pendingRangeKind As String = ""
        Private _pendingRangeSampleX As Double
        Private _pendingRangeSampleY As Double

        ''' <summary>Überträgt die gerade erzeugte Bereichsauswahl auf eine bereits geöffnete
        ''' Maskenebene. Das verhindert, dass ein Reglerzug eine neue Ebene anlegt, und bewahrt
        ''' zugleich die Parameter für das nächste Öffnen.</summary>
        Private Sub PersistRangeMaskIfEditing()
            Dim target = EditedLayerMask()
            If target Is Nothing OrElse String.IsNullOrWhiteSpace(_pendingRangeKind) Then Return
            Dim fresh = ImageProcessor.CreateSourceMaskFromSelection(BuildAdjustmentsFromFields(), target.Name)
            If fresh Is Nothing Then Return
            target.Left = fresh.Left : target.Top = fresh.Top : target.Right = fresh.Right : target.Bottom = fresh.Bottom
            target.PngBase64 = fresh.PngBase64 : target.FeatherPixels = fresh.FeatherPixels
            target.RangeKind = _pendingRangeKind
            target.RangeTolerance = _colorRangeTolerance
            target.RangeFeather = If(_pendingRangeKind = "Luminance", _luminanceRangeFeather,
                                     If(_pendingRangeKind = "Depth", _depthFeather, _colorRangeFeather))
            target.RangeFrom = If(_pendingRangeKind = "Depth", _depthFrom, _luminanceRangeFrom)
            target.RangeTo = If(_pendingRangeKind = "Depth", _depthTo, _luminanceRangeTo)
            target.RangeSampleXPercent = _pendingRangeSampleX : target.RangeSampleYPercent = _pendingRangeSampleY
            target.RangeContiguous = _colorRangeContiguous
            _hasChanges = True
            SchedulePreviewUpdate()
        End Sub

        ''' <summary>Wird beim erstmaligen Anlegen einer Bereichsmaske aufgerufen, nachdem die
        ''' Auswahl zu einer Ebene promotet wurde.</summary>
        Private Sub ApplyPendingRangeMetadata(mask As ImageMask)
            If mask Is Nothing OrElse String.IsNullOrWhiteSpace(_pendingRangeKind) Then Return
            mask.RangeKind = _pendingRangeKind
            mask.RangeTolerance = _colorRangeTolerance
            mask.RangeFeather = If(_pendingRangeKind = "Luminance", _luminanceRangeFeather,
                                   If(_pendingRangeKind = "Depth", _depthFeather, _colorRangeFeather))
            mask.RangeFrom = If(_pendingRangeKind = "Depth", _depthFrom, _luminanceRangeFrom)
            mask.RangeTo = If(_pendingRangeKind = "Depth", _depthTo, _luminanceRangeTo)
            mask.RangeSampleXPercent = _pendingRangeSampleX : mask.RangeSampleYPercent = _pendingRangeSampleY
            mask.RangeContiguous = _colorRangeContiguous
            _pendingRangeKind = ""
        End Sub

        Public Property ColorRangeTolerance As Double
            Get
                Return _colorRangeTolerance
            End Get
            Set(value As Double)
                _colorRangeTolerance = Math.Max(0.0, Math.Min(100.0, value))
                Me.RaisePropertyChanged(NameOf(ColorRangeTolerance))
                Dim mask = EditedLayerMask()
                If IsMaskColorRangeMode AndAlso mask IsNot Nothing AndAlso mask.RangeKind = "Color" Then
                    Dim ignored = SetSelectionColorRangeMask(mask.RangeSampleXPercent, mask.RangeSampleYPercent)
                End If
            End Set
        End Property

        Public Property ColorRangeFeather As Double
            Get
                Return _colorRangeFeather
            End Get
            Set(value As Double)
                _colorRangeFeather = Math.Max(1.0, Math.Min(100.0, value))
                Me.RaisePropertyChanged(NameOf(ColorRangeFeather))
                Dim mask = EditedLayerMask()
                If IsMaskColorRangeMode AndAlso mask IsNot Nothing AndAlso mask.RangeKind = "Color" Then
                    Dim ignored = SetSelectionColorRangeMask(mask.RangeSampleXPercent, mask.RangeSampleYPercent)
                End If
            End Set
        End Property

        Public Property ColorRangeContiguous As Boolean
            Get
                Return _colorRangeContiguous
            End Get
            Set(value As Boolean)
                If _colorRangeContiguous = value Then Return
                _colorRangeContiguous = value
                Me.RaisePropertyChanged(NameOf(ColorRangeContiguous))
                Dim mask = EditedLayerMask()
                If IsMaskColorRangeMode AndAlso mask IsNot Nothing AndAlso mask.RangeKind = "Color" Then
                    Dim ignored = SetSelectionColorRangeMask(mask.RangeSampleXPercent, mask.RangeSampleYPercent)
                End If
            End Set
        End Property

        Public Property LuminanceRangeFrom As Double
            Get
                Return _luminanceRangeFrom
            End Get
            Set(value As Double)
                _luminanceRangeFrom = Math.Max(0.0, Math.Min(100.0, value))
                If _luminanceRangeFrom > _luminanceRangeTo Then _luminanceRangeTo = _luminanceRangeFrom
                Me.RaisePropertyChanged(NameOf(LuminanceRangeFrom))
                Me.RaisePropertyChanged(NameOf(LuminanceRangeTo))
                If IsMaskLuminanceRangeMode Then
                    Dim ignored = RedrawLuminanceRangeMask()
                ElseIf IsLuminanceRangeSelectionMode Then
                    Dim ignored = RedrawLuminanceRangeMask(isMask:=False)
                End If
            End Set
        End Property

        Public Property LuminanceRangeTo As Double
            Get
                Return _luminanceRangeTo
            End Get
            Set(value As Double)
                _luminanceRangeTo = Math.Max(0.0, Math.Min(100.0, value))
                If _luminanceRangeTo < _luminanceRangeFrom Then _luminanceRangeFrom = _luminanceRangeTo
                Me.RaisePropertyChanged(NameOf(LuminanceRangeTo))
                Me.RaisePropertyChanged(NameOf(LuminanceRangeFrom))
                If IsMaskLuminanceRangeMode Then
                    Dim ignored = RedrawLuminanceRangeMask()
                ElseIf IsLuminanceRangeSelectionMode Then
                    Dim ignored = RedrawLuminanceRangeMask(isMask:=False)
                End If
            End Set
        End Property

        Public Property LuminanceRangeFeather As Double
            Get
                Return _luminanceRangeFeather
            End Get
            Set(value As Double)
                _luminanceRangeFeather = Math.Max(0.0, Math.Min(50.0, value))
                Me.RaisePropertyChanged(NameOf(LuminanceRangeFeather))
                If IsMaskLuminanceRangeMode Then
                    Dim ignored = RedrawLuminanceRangeMask()
                ElseIf IsLuminanceRangeSelectionMode Then
                    Dim ignored = RedrawLuminanceRangeMask(isMask:=False)
                End If
            End Set
        End Property

        Public Async Function SetSelectionColorRangeMask(xPercent As Double, yPercent As Double,
                                                         Optional isMask As Boolean = True) As Task
            If String.IsNullOrWhiteSpace(_currentImagePath) Then Return
            Await _rangeMaskGate.WaitAsync()
            Try
                Dim size = GetAnnotationDisplayPixelSize()
                If size.Width <= 0 OrElse size.Height <= 0 Then Return
                Dim x = Math.Max(0, Math.Min(size.Width - 1, CInt(Math.Round(xPercent / 100.0 * size.Width))))
                Dim y = Math.Max(0, Math.Min(size.Height - 1, CInt(Math.Round(yPercent / 100.0 * size.Height))))
                Dim sourcePath = RenderSourcePath, adjustments = GetCurrentAdjustments(), working = CloneWorkingFullForRender()
                Dim tolerance = _colorRangeTolerance, feather = _colorRangeFeather, contiguous = _colorRangeContiguous
                Dim editingMaskId = _editingLayerMaskId
                Dim result = Await Task.Run(Function()
                                                Using rendered = ImageProcessor.RenderDisplayImage(sourcePath, adjustments, working)
                                                    If rendered Is Nothing Then Return (Mask:=DirectCast(Nothing, SKBitmap), Bounds:=SKRectI.Empty)
                                                    Dim bounds As SKRectI
                                                    Dim mask = If(contiguous,
                                                        ImageProcessor.BuildMagicWandMask(rendered, x, y, CSng(tolerance / 100.0), bounds),
                                                        ImageProcessor.BuildColorRangeMask(rendered, x, y, tolerance, feather, bounds))
                                                    Return (Mask:=mask, Bounds:=bounds)
                                                End Using
                                            End Function)
                Using mask = result.Mask
                    If mask Is Nothing Then Return
                    Using cut = ExtractMaskRegion(mask, result.Bounds)
                        If cut Is Nothing Then Return
                        If isMask Then _pendingRangeKind = "Color" : _pendingRangeSampleX = xPercent : _pendingRangeSampleY = yPercent
                        PushUndo()
                        ApplySelectionCandidate(cut, result.Bounds, "MagicWand", Nothing, Nothing, isMask:=isMask, forceNew:=isMask)
                        If editingMaskId <> "" Then _editingLayerMaskId = editingMaskId
                    End Using
                End Using
                If isMask Then PersistRangeMaskIfEditing()
                StatusText = LocalizationService.T("Farbbereichsmaske gesetzt")
            Finally
                _rangeMaskGate.Release()
            End Try
        End Function

        Public Async Function RedrawLuminanceRangeMask(Optional isMask As Boolean = True) As Task
            If String.IsNullOrWhiteSpace(_currentImagePath) Then Return
            Await _rangeMaskGate.WaitAsync()
            Try
                Dim sourcePath = RenderSourcePath, adjustments = GetCurrentAdjustments(), working = CloneWorkingFullForRender()
                Dim from = _luminanceRangeFrom, [to] = _luminanceRangeTo, feather = _luminanceRangeFeather
                Dim editingMaskId = _editingLayerMaskId
                Dim result = Await Task.Run(Function()
                                                Using rendered = ImageProcessor.RenderDisplayImage(sourcePath, adjustments, working)
                                                    If rendered Is Nothing Then Return (Mask:=DirectCast(Nothing, SKBitmap), Bounds:=SKRectI.Empty)
                                                    Dim bounds As SKRectI
                                                    Dim mask = ImageProcessor.BuildLuminanceRangeMask(rendered, from, [to], feather, bounds)
                                                    Return (Mask:=mask, Bounds:=bounds)
                                                End Using
                                            End Function)
                Using mask = result.Mask
                    If mask Is Nothing Then Return
                    Using cut = ExtractMaskRegion(mask, result.Bounds)
                        If cut Is Nothing Then Return
                        If isMask Then _pendingRangeKind = "Luminance" : _pendingRangeSampleX = 0 : _pendingRangeSampleY = 0
                        ApplySelectionCandidate(cut, result.Bounds, "MagicWand", Nothing, Nothing, isMask:=isMask, forceNew:=isMask)
                        If editingMaskId <> "" Then _editingLayerMaskId = editingMaskId
                    End Using
                End Using
                If isMask Then PersistRangeMaskIfEditing()
                StatusText = LocalizationService.T("Luminanzbereichsmaske gesetzt")
            Finally
                _rangeMaskGate.Release()
            End Try
        End Function
    End Class

End Namespace
