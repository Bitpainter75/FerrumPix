Option Strict On
Option Explicit On

Imports System.Collections.ObjectModel
Imports System.Windows.Input
Imports FerrumPix.Services
Imports ReactiveUI
Imports SkiaSharp

' Das Werkzeug Beleuchtung: frei platzierbare Lichtquellen (und Schatten) auf dem Bild. Die Lichter
' stehen im Rezept (ImageAdjustments.LightSources), gerechnet werden sie in ImageProcessorLights.vb,
' gezogen werden sie auf der Buehne ueber Controls/LightSourceOverlay.vb.
Namespace ViewModels

    ''' <summary>Eine Zeile der Lichterliste im Panel. Meldet ihre Aenderungen selbst: die Liste
    ''' wird nicht bei jeder Aenderung neu aufgebaut, sonst verschwaende der Knopf unter dem
    ''' Zeiger zwischen Druecken und Loslassen und der Klick ginge verloren.</summary>
    Public NotInheritable Class LightRow
        Inherits ReactiveObject

        Public Property Id As String

        Private _label As String
        Public Property Label As String
            Get
                Return _label
            End Get
            Set(value As String)
                Me.RaiseAndSetIfChanged(_label, value)
            End Set
        End Property

        Private _isSelected As Boolean
        Public Property IsSelected As Boolean
            Get
                Return _isSelected
            End Get
            Set(value As Boolean)
                Me.RaiseAndSetIfChanged(_isSelected, value)
            End Set
        End Property

        Private _isEnabled As Boolean
        Public Property IsEnabled As Boolean
            Get
                Return _isEnabled
            End Get
            Set(value As Boolean)
                Me.RaiseAndSetIfChanged(_isEnabled, value)
            End Set
        End Property

        Private _iconSource As String
        Public Property IconSource As String
            Get
                Return _iconSource
            End Get
            Set(value As String)
                Me.RaiseAndSetIfChanged(_iconSource, value)
            End Set
        End Property

        Private _swatchColor As Avalonia.Media.Color
        Private _swatchBrush As Avalonia.Media.IBrush
        Public ReadOnly Property SwatchBrush As Avalonia.Media.IBrush
            Get
                Return _swatchBrush
            End Get
        End Property

        Public Sub SetSwatch(color As Avalonia.Media.Color)
            If _swatchBrush IsNot Nothing AndAlso color = _swatchColor Then Return
            _swatchColor = color
            _swatchBrush = New Avalonia.Media.SolidColorBrush(color)
            Me.RaisePropertyChanged(NameOf(SwatchBrush))
        End Sub
    End Class

    Partial Public Class EditorViewModel

        Private ReadOnly _lightSources As New List(Of LightSource)()
        Private _selectedLightId As String = ""
        Private _newLightPresetKey As String = "Bulb"
        ''' <summary>Der Schluessel der zuletzt abgelegten Tiefenkarte (ImageProcessor.LightDepthKey).
        ''' Ins Rezept geht er nur, solange er zum Rezept passt: nach einem Bildwechsel, einer
        ''' Geometrieaenderung oder einer Retusche gehoert die Karte nicht mehr zum Bild.</summary>
        Private _lightDepthToken As String = ""
        ''' <summary>Der Schluessel, fuer den die Rechnung zuletzt nichts lieferte. Fuer ihn wird
        ''' nicht von selbst neu gerechnet, sonst liefe eine scheiternde Rechnung endlos.</summary>
        Private _lightDepthFailedKey As String = ""
        Private _lightEditUndoPending As Boolean
        Private _lightOverlayVersion As Integer
        Private _lightEditActive As Boolean

        Public ReadOnly Property LightRows As New ObservableCollection(Of LightRow)()

        ''' <summary>Steigt bei jeder Aenderung an einem Licht; die Buehne zeichnet ihre Griffe neu.</summary>
        Public ReadOnly Property LightOverlayVersion As Integer
            Get
                Return _lightOverlayVersion
            End Get
        End Property

        Public ReadOnly Property ShowLightControls As Boolean
            Get
                Return _currentTool = EditorTool.Light
            End Get
        End Property

        Public ReadOnly Property HasLights As Boolean
            Get
                Return _lightSources.Count > 0
            End Get
        End Property

        Public ReadOnly Property HasSelectedLight As Boolean
            Get
                Return SelectedLight IsNot Nothing
            End Get
        End Property

        ''' <summary>Die Lichter fuer die Buehne, als Kopie: sie zeichnet, waehrend hier geaendert wird.</summary>
        Friend Function LightSnapshot() As List(Of LightSource)
            Return _lightSources.Select(Function(l) l.Clone()).ToList()
        End Function

        Friend ReadOnly Property SelectedLightId As String
            Get
                Return _selectedLightId
            End Get
        End Property

        Private ReadOnly Property SelectedLight As LightSource
            Get
                Return _lightSources.FirstOrDefault(Function(l) l.Id = _selectedLightId)
            End Get
        End Property

        ' ── Lichtart fuer neue Lichter ──────────────────────────────────────────

        Public ReadOnly Property LightPresetOptions As IReadOnlyList(Of String)
            Get
                Return LightPresets.All.Select(Function(p) p.Label).ToList()
            End Get
        End Property

        ''' <summary>Die Lichtart, mit der ein Klick ins Bild ein neues Licht setzt. Ist ein Licht
        ''' markiert, bekommt es die Art gleich mit.</summary>
        Public Property LightPresetLabel As String
            Get
                Dim key = If(SelectedLight IsNot Nothing AndAlso Not String.IsNullOrEmpty(SelectedLight.Preset),
                             SelectedLight.Preset, _newLightPresetKey)
                Return If(LightPresets.Find(key)?.Label, LightPresets.All(0).Label)
            End Get
            Set(value As String)
                Dim preset = LightPresets.All.FirstOrDefault(Function(p) p.Label = value)
                If preset Is Nothing Then Return
                _newLightPresetKey = preset.Key
                Dim light = SelectedLight
                If light IsNot Nothing AndAlso light.Preset <> preset.Key Then
                    CaptureUndoState(NameOf(LightPresetLabel))
                    RenameWithPreset(light, preset)
                    preset.ApplyTo(light)
                    AfterLightChanged(raiseAll:=True)
                Else
                    Me.RaisePropertyChanged(NameOf(LightPresetLabel))
                End If
            End Set
        End Property

        ' ── Liste, Auswahl, Anlegen, Entfernen ──────────────────────────────────

        Public ReadOnly Property AddLightCommand As ICommand = ReactiveCommand.Create(Sub() AddLightAt(50, 40))
        Public ReadOnly Property RemoveLightCommand As ICommand = ReactiveCommand.Create(Sub() RemoveSelectedLight())
        ' Die Befehle der Zeilen sind einfache DelegateCommands: der Parameter kommt aus der Bindung
        ' der Zeile als Object und wird hier zur Kennung.
        Public ReadOnly Property RemoveLightByIdCommand As ICommand = New DelegateCommand(Sub(p) RemoveLight(TryCast(p, String)))
        Public ReadOnly Property DuplicateLightCommand As ICommand = ReactiveCommand.Create(Sub() DuplicateSelectedLight())
        Public ReadOnly Property SelectLightCommand As ICommand = New DelegateCommand(Sub(p) SelectLight(TryCast(p, String)))
        Public ReadOnly Property ToggleLightEnabledCommand As ICommand = New DelegateCommand(Sub(p) ToggleLightEnabled(TryCast(p, String)))
        Public ReadOnly Property ResetLightsCommand As ICommand = ReactiveCommand.Create(Sub() ResetLights())

        ''' <summary>Setzt ein neues Licht der gewaehlten Lichtart an diese Stelle (Prozent des Bildes).</summary>
        Public Function AddLightAt(xPercent As Double, yPercent As Double) As LightSource
            CaptureUndoState("LightAdd")
            Dim light As New LightSource With {.XPercent = xPercent, .YPercent = yPercent}
            Dim preset = If(LightPresets.Find(_newLightPresetKey), LightPresets.All(0))
            preset.ApplyTo(light)
            light.Name = NextLightName(LocalizationService.T(preset.Label))
            _lightSources.Add(light)
            _selectedLightId = light.Id
            AfterLightChanged(raiseAll:=True)
            Return light
        End Function

        ''' <summary>Traegt das Licht noch den Namen, den es beim Anlegen bekam („Glühbirne 1“), geht
        ''' er mit der Lichtart mit („Flutlicht 1“). Ein selbst vergebener Name bleibt stehen.</summary>
        Private Shared Sub RenameWithPreset(light As LightSource, preset As LightPreset)
            Dim oldLabel = LocalizationService.T(If(LightPresets.Find(light.Preset)?.Label, LightSourceKinds.Label(light.Kind)))
            Dim name = If(light.Name, "")
            Dim number = ""
            If name = oldLabel Then
                number = ""
            ElseIf name.StartsWith(oldLabel & " ", StringComparison.Ordinal) AndAlso
                   name.Substring(oldLabel.Length + 1).All(AddressOf Char.IsDigit) Then
                number = name.Substring(oldLabel.Length + 1)
            Else
                Return
            End If
            Dim newLabel = LocalizationService.T(preset.Label)
            light.Name = If(number = "", newLabel, newLabel & " " & number)
        End Sub

        Private Function NextLightName(label As String) As String
            Dim n = _lightSources.Count + 1
            Return label & " " & n.ToString(Globalization.CultureInfo.InvariantCulture)
        End Function

        Public Sub SelectLight(id As String)
            If String.Equals(_selectedLightId, If(id, ""), StringComparison.Ordinal) Then Return
            _selectedLightId = If(id, "")
            AfterLightChanged(raiseAll:=True, render:=False)
        End Sub

        Private Sub RemoveSelectedLight()
            RemoveLight(_selectedLightId)
        End Sub

        ''' <summary>Entfernt ein Licht, das X seiner Zeile oder ENTF. War es markiert, rueckt das
        ''' naechste nach.</summary>
        Private Sub RemoveLight(id As String)
            Dim light = _lightSources.FirstOrDefault(Function(l) l.Id = id)
            If light Is Nothing Then Return
            CaptureUndoState("LightRemove")
            Dim index = _lightSources.IndexOf(light)
            _lightSources.Remove(light)
            If light.Id = _selectedLightId OrElse _lightSources.All(Function(l) l.Id <> _selectedLightId) Then
                _selectedLightId = If(_lightSources.Count = 0, "", _lightSources(Math.Min(index, _lightSources.Count - 1)).Id)
            End If
            AfterLightChanged(raiseAll:=True)
        End Sub

        Private Sub DuplicateSelectedLight()
            Dim light = SelectedLight
            If light Is Nothing Then Return
            CaptureUndoState("LightAdd")
            Dim copy = light.Clone()
            copy.Id = Guid.NewGuid().ToString("N")
            copy.XPercent = Math.Min(100, light.XPercent + 5)
            copy.YPercent = Math.Min(100, light.YPercent + 5)
            copy.Name = NextLightName(LocalizationService.T(If(LightPresets.Find(light.Preset)?.Label, LightSourceKinds.Label(light.Kind))))
            _lightSources.Add(copy)
            _selectedLightId = copy.Id
            AfterLightChanged(raiseAll:=True)
        End Sub

        Private Sub ToggleLightEnabled(id As String)
            Dim light = _lightSources.FirstOrDefault(Function(l) l.Id = id)
            If light Is Nothing Then Return
            CaptureUndoState("LightEnabled")
            light.Enabled = Not light.Enabled
            AfterLightChanged(raiseAll:=False)
        End Sub

        Private Sub ResetLights()
            If _lightSources.Count = 0 Then Return
            CaptureUndoState("LightRemove")
            _lightSources.Clear()
            _selectedLightId = ""
            AfterLightChanged(raiseAll:=True)
        End Sub

        ''' <summary>Nach jeder Aenderung: Liste, Buehne, Regler und Vorschau nachziehen.</summary>
        Private Sub AfterLightChanged(raiseAll As Boolean, Optional render As Boolean = True)
            _lightOverlayVersion += 1
            Me.RaisePropertyChanged(NameOf(LightOverlayVersion))
            RebuildLightRows()
            If raiseAll Then RaiseLightProperties()
            If render Then
                _hasChanges = True
                RaiseResetButtonStateChanged()
                SchedulePreviewUpdate()
                If _lightSources.Any(Function(l) l.UseDepth AndAlso l.Enabled) Then
                    Dim ignored = EnsureLightDepthMapAsync()
                End If
            End If
        End Sub

        ''' <summary>Gleicht die Zeilen mit den Lichtern ab. Bestehende Zeilen werden nur
        ''' aktualisiert, neu gebaut wird lediglich, wenn Lichter dazukommen, wegfallen oder die
        ''' Reihenfolge wechselt.</summary>
        Private Sub RebuildLightRows()
            Dim sameSet = LightRows.Count = _lightSources.Count AndAlso
                          LightRows.Select(Function(r) r.Id).SequenceEqual(_lightSources.Select(Function(l) l.Id))
            If Not sameSet Then
                LightRows.Clear()
                For Each l In _lightSources
                    LightRows.Add(New LightRow With {.Id = l.Id})
                Next
            End If
            For i = 0 To _lightSources.Count - 1
                Dim l = _lightSources(i)
                Dim row = LightRows(i)
                row.Label = If(String.IsNullOrWhiteSpace(l.Name), LocalizationService.T(LightSourceKinds.Label(l.Kind)), l.Name)
                row.IsSelected = l.Id = _selectedLightId
                row.IsEnabled = l.Enabled
                row.IconSource = IconRoot & "phosphor/regular/" & If(l.Intensity < 0, "cloud.svg", LightKindIcon(l.Kind))
                row.SetSwatch(LightSwatchColor(l))
            Next
            Me.RaisePropertyChanged(NameOf(HasLights))
            Me.RaisePropertyChanged(NameOf(HasSelectedLight))
        End Sub

        Friend Shared Function LightKindIcon(kind As String) As String
            Select Case kind
                Case LightSourceKinds.Spot : Return "flashlight.svg"
                Case LightSourceKinds.Area : Return "square-half.svg"
                Case LightSourceKinds.Streak : Return "sun-horizon.svg"
                Case LightSourceKinds.Rays : Return "sun.svg"
                Case Else : Return "lightbulb.svg"
            End Select
        End Function

        Friend Shared Function LightSwatchColor(l As LightSource) As Avalonia.Media.Color
            If l.UseTemperature Then
                Dim rgb = ImageProcessor.KelvinToRgb(l.TemperatureKelvin)
                Return Avalonia.Media.Color.FromRgb(CByte(rgb.R * 255), CByte(rgb.G * 255), CByte(rgb.B * 255))
            End If
            Dim c As SKColor
            If Not SKColor.TryParse(If(l.Color, ""), c) Then c = SKColors.White
            Return Avalonia.Media.Color.FromRgb(c.Red, c.Green, c.Blue)
        End Function

        Private Sub RaiseLightProperties()
            For Each name In {NameOf(HasSelectedLight), NameOf(LightPresetLabel), NameOf(LightKindLabel), NameOf(LightIntensity),
                              NameOf(LightRadius), NameOf(LightAngle), NameOf(LightCone), NameOf(LightAspect), NameOf(LightSoftness),
                              NameOf(LightFalloffLabel), NameOf(LightUseTemperature), NameOf(LightUseColor), NameOf(LightTemperature),
                              NameOf(LightColorValue), NameOf(LightBlendModeLabel), NameOf(LightGlow), NameOf(LightRayCount),
                              NameOf(LightRayContrast), NameOf(LightUseDepth), NameOf(LightDepthReach), NameOf(LightX), NameOf(LightY),
                              NameOf(IsLightSpot), NameOf(IsLightArea), NameOf(IsLightRays), NameOf(IsLightDirectional),
                              NameOf(LightSwatchBrush), NameOf(LightName)}
                Me.RaisePropertyChanged(name)
            Next
        End Sub

        ' ── Die Regler des markierten Lichts ────────────────────────────────────

        ''' <summary>Aendert einen Wert des markierten Lichts mit einem Schritt in der Historie.
        ''' Waehrend eines Reglerzuges fasst CaptureUndoState die Schritte zusammen.</summary>
        Private Sub SetLightValue(propertyName As String, change As Action(Of LightSource), unchanged As Func(Of LightSource, Boolean),
                                  Optional raiseAll As Boolean = False)
            Dim light = SelectedLight
            If light Is Nothing OrElse unchanged(light) Then Return
            If Not _lightEditActive Then CaptureUndoState(propertyName)
            change(light)
            Me.RaisePropertyChanged(propertyName)
            AfterLightChanged(raiseAll)
        End Sub

        Public Property LightName As String
            Get
                Return If(SelectedLight?.Name, "")
            End Get
            Set(value As String)
                SetLightValue(NameOf(LightName), Sub(l) l.Name = If(value, ""), Function(l) l.Name = If(value, ""))
            End Set
        End Property

        Public ReadOnly Property LightKindOptions As IReadOnlyList(Of String)
            Get
                Return LightSourceKinds.All.Select(Function(k) LightSourceKinds.Label(k)).ToList()
            End Get
        End Property

        Public Property LightKindLabel As String
            Get
                Return LightSourceKinds.Label(If(SelectedLight?.Kind, LightSourceKinds.Point))
            End Get
            Set(value As String)
                Dim kind = LightSourceKinds.All.FirstOrDefault(Function(k) LightSourceKinds.Label(k) = value)
                If kind Is Nothing Then Return
                SetLightValue(NameOf(LightKindLabel), Sub(l) l.Kind = kind, Function(l) l.Kind = kind, raiseAll:=True)
            End Set
        End Property

        Public ReadOnly Property IsLightSpot As Boolean
            Get
                Return SelectedLight?.Kind = LightSourceKinds.Spot
            End Get
        End Property

        Public ReadOnly Property IsLightArea As Boolean
            Get
                Return SelectedLight?.Kind = LightSourceKinds.Area
            End Get
        End Property

        Public ReadOnly Property IsLightRays As Boolean
            Get
                Return SelectedLight?.Kind = LightSourceKinds.Rays
            End Get
        End Property

        ''' <summary>Hat die Art eine Richtung? Punktlicht und Strahlen strahlen rundum.</summary>
        Public ReadOnly Property IsLightDirectional As Boolean
            Get
                Dim k = SelectedLight?.Kind
                Return k = LightSourceKinds.Spot OrElse k = LightSourceKinds.Area OrElse k = LightSourceKinds.Streak
            End Get
        End Property

        Public Property LightIntensity As Double
            Get
                Return If(SelectedLight?.Intensity, 1.0)
            End Get
            Set(value As Double)
                Dim v = Math.Max(-4.0, Math.Min(4.0, value))
                SetLightValue(NameOf(LightIntensity), Sub(l) l.Intensity = v, Function(l) Math.Abs(l.Intensity - v) < 0.0001)
            End Set
        End Property

        Public Property LightRadius As Double
            Get
                Return If(SelectedLight?.RadiusPercent, 30.0)
            End Get
            Set(value As Double)
                Dim v = Math.Max(1.0, Math.Min(300.0, value))
                SetLightValue(NameOf(LightRadius), Sub(l) l.RadiusPercent = v, Function(l) Math.Abs(l.RadiusPercent - v) < 0.0001)
            End Set
        End Property

        Public Property LightAngle As Double
            Get
                Return If(SelectedLight?.AngleDegrees, 90.0)
            End Get
            Set(value As Double)
                Dim v = NormalizeLightAngle(value)
                SetLightValue(NameOf(LightAngle), Sub(l) l.AngleDegrees = v, Function(l) Math.Abs(l.AngleDegrees - v) < 0.0001)
            End Set
        End Property

        Private Shared Function NormalizeLightAngle(value As Double) As Double
            Dim v = value Mod 360.0
            If v > 180 Then v -= 360
            If v <= -180 Then v += 360
            Return v
        End Function

        Public Property LightCone As Double
            Get
                Return If(SelectedLight?.ConeDegrees, 50.0)
            End Get
            Set(value As Double)
                Dim v = Math.Max(2.0, Math.Min(170.0, value))
                SetLightValue(NameOf(LightCone), Sub(l) l.ConeDegrees = v, Function(l) Math.Abs(l.ConeDegrees - v) < 0.0001)
            End Set
        End Property

        Public Property LightAspect As Double
            Get
                Return If(SelectedLight?.AspectPercent, 100.0)
            End Get
            Set(value As Double)
                Dim v = Math.Max(10.0, Math.Min(1000.0, value))
                SetLightValue(NameOf(LightAspect), Sub(l) l.AspectPercent = v, Function(l) Math.Abs(l.AspectPercent - v) < 0.0001)
            End Set
        End Property

        Public Property LightSoftness As Double
            Get
                Return If(SelectedLight?.Softness, 50.0)
            End Get
            Set(value As Double)
                Dim v = Math.Max(0.0, Math.Min(100.0, value))
                SetLightValue(NameOf(LightSoftness), Sub(l) l.Softness = v, Function(l) Math.Abs(l.Softness - v) < 0.0001)
            End Set
        End Property

        Public ReadOnly Property LightFalloffOptions As IReadOnlyList(Of String)
            Get
                Return LightFalloffs.All.Select(Function(f) LightFalloffs.Label(f)).ToList()
            End Get
        End Property

        Public Property LightFalloffLabel As String
            Get
                Return LightFalloffs.Label(If(SelectedLight?.Falloff, LightFalloffs.Natural))
            End Get
            Set(value As String)
                Dim f = LightFalloffs.All.FirstOrDefault(Function(x) LightFalloffs.Label(x) = value)
                If f Is Nothing Then Return
                SetLightValue(NameOf(LightFalloffLabel), Sub(l) l.Falloff = f, Function(l) l.Falloff = f)
            End Set
        End Property

        Public Property LightUseTemperature As Boolean
            Get
                Return If(SelectedLight?.UseTemperature, True)
            End Get
            Set(value As Boolean)
                SetLightValue(NameOf(LightUseTemperature), Sub(l) l.UseTemperature = value, Function(l) l.UseTemperature = value, raiseAll:=True)
            End Set
        End Property

        Public ReadOnly Property LightUseColor As Boolean
            Get
                Return Not LightUseTemperature
            End Get
        End Property

        Public Property LightTemperature As Double
            Get
                Return If(SelectedLight?.TemperatureKelvin, 3200.0)
            End Get
            Set(value As Double)
                Dim v = Math.Max(1000.0, Math.Min(12000.0, value))
                SetLightValue(NameOf(LightTemperature), Sub(l) l.TemperatureKelvin = v, Function(l) Math.Abs(l.TemperatureKelvin - v) < 0.5)
                Me.RaisePropertyChanged(NameOf(LightSwatchBrush))
            End Set
        End Property

        Public Property LightColorValue As Avalonia.Media.Color
            Get
                Dim l = SelectedLight
                If l Is Nothing Then Return Avalonia.Media.Colors.White
                Dim c As SKColor
                If Not SKColor.TryParse(If(l.Color, ""), c) Then c = SKColors.White
                Return Avalonia.Media.Color.FromArgb(255, c.Red, c.Green, c.Blue)
            End Get
            Set(value As Avalonia.Media.Color)
                Dim hex = $"#FF{value.R:X2}{value.G:X2}{value.B:X2}"
                SetLightValue(NameOf(LightColorValue), Sub(l) l.Color = hex, Function(l) String.Equals(l.Color, hex, StringComparison.OrdinalIgnoreCase))
                Me.RaisePropertyChanged(NameOf(LightSwatchBrush))
            End Set
        End Property

        Public ReadOnly Property LightSwatchBrush As Avalonia.Media.IBrush
            Get
                Dim l = SelectedLight
                Return New Avalonia.Media.SolidColorBrush(If(l Is Nothing, Avalonia.Media.Colors.White, LightSwatchColor(l)))
            End Get
        End Property

        Public ReadOnly Property LightBlendModeOptions As IReadOnlyList(Of String)
            Get
                Return LightBlendModes.All.Select(Function(m) LightBlendModes.Label(m)).ToList()
            End Get
        End Property

        Public Property LightBlendModeLabel As String
            Get
                Return LightBlendModes.Label(If(SelectedLight?.BlendMode, LightBlendModes.Illuminate))
            End Get
            Set(value As String)
                Dim m = LightBlendModes.All.FirstOrDefault(Function(x) LightBlendModes.Label(x) = value)
                If m Is Nothing Then Return
                SetLightValue(NameOf(LightBlendModeLabel), Sub(l) l.BlendMode = m, Function(l) l.BlendMode = m)
            End Set
        End Property

        Public Property LightGlow As Double
            Get
                Return If(SelectedLight?.Glow, 0.0)
            End Get
            Set(value As Double)
                Dim v = Math.Max(0.0, Math.Min(100.0, value))
                SetLightValue(NameOf(LightGlow), Sub(l) l.Glow = v, Function(l) Math.Abs(l.Glow - v) < 0.0001)
            End Set
        End Property

        Public Property LightRayCount As Double
            Get
                Return If(SelectedLight?.RayCount, 14.0)
            End Get
            Set(value As Double)
                Dim v = Math.Max(3.0, Math.Min(60.0, Math.Round(value)))
                SetLightValue(NameOf(LightRayCount), Sub(l) l.RayCount = v, Function(l) Math.Abs(l.RayCount - v) < 0.0001)
            End Set
        End Property

        Public Property LightRayContrast As Double
            Get
                Return If(SelectedLight?.RayContrast, 60.0)
            End Get
            Set(value As Double)
                Dim v = Math.Max(0.0, Math.Min(100.0, value))
                SetLightValue(NameOf(LightRayContrast), Sub(l) l.RayContrast = v, Function(l) Math.Abs(l.RayContrast - v) < 0.0001)
            End Set
        End Property

        Public Property LightX As Double
            Get
                Return If(SelectedLight?.XPercent, 50.0)
            End Get
            Set(value As Double)
                Dim v = Math.Max(-50.0, Math.Min(150.0, value))
                SetLightValue(NameOf(LightX), Sub(l) l.XPercent = v, Function(l) Math.Abs(l.XPercent - v) < 0.0001)
            End Set
        End Property

        Public Property LightY As Double
            Get
                Return If(SelectedLight?.YPercent, 50.0)
            End Get
            Set(value As Double)
                Dim v = Math.Max(-50.0, Math.Min(150.0, value))
                SetLightValue(NameOf(LightY), Sub(l) l.YPercent = v, Function(l) Math.Abs(l.YPercent - v) < 0.0001)
            End Set
        End Property

        ' ── Tiefe ────────────────────────────────────────────────────────────────

        Public ReadOnly Property IsDepthModelAvailable As Boolean
            Get
                Return DepthMapService.Available
            End Get
        End Property

        Public Property LightUseDepth As Boolean
            Get
                Return If(SelectedLight?.UseDepth, False)
            End Get
            Set(value As Boolean)
                SetLightValue(NameOf(LightUseDepth), Sub(l) l.UseDepth = value, Function(l) l.UseDepth = value)
                If value AndAlso Not DepthMapService.Available Then
                    ' Ohne Modell wuerde der Schalter nichts tun. Gefragt wird wie bei Nebel und
                    ' Tiefen-Unschaerfe; ohne Download geht er zurueck.
                    RequestDepthForPreview(LocalizationService.T("Beleuchtung"),
                                           Sub() LightUseDepth = False,
                                           Sub() AfterLightChanged(raiseAll:=True))
                End If
            End Set
        End Property

        Public Property LightDepthReach As Double
            Get
                Return If(SelectedLight?.DepthReach, 25.0)
            End Get
            Set(value As Double)
                Dim v = Math.Max(0.0, Math.Min(100.0, value))
                SetLightValue(NameOf(LightDepthReach), Sub(l) l.DepthReach = v, Function(l) Math.Abs(l.DepthReach - v) < 0.0001)
            End Set
        End Property

        Private _lightDepthRunning As Boolean

        ''' <summary>Das Tiefenzeichen fuer das Rezept: nur, wenn die abgelegte Karte zu genau diesem
        ''' Bildstand gehoert. Passt sie nicht mehr und braucht ein Licht die Tiefe, wird die Karte
        ''' fuer den neuen Stand angestossen; bis sie da ist, rechnen die Lichter ohne Tiefe.</summary>
        Private Function LightDepthTokenFor(adj As ImageAdjustments) As String
            If String.IsNullOrEmpty(_lightDepthToken) Then Return ""
            Dim key = ImageProcessor.LightDepthKey(_currentImagePath, adj)
            If String.Equals(key, _lightDepthToken, StringComparison.Ordinal) Then Return _lightDepthToken
            If Not _lightDepthRunning AndAlso Not String.Equals(key, _lightDepthFailedKey, StringComparison.Ordinal) AndAlso
               _lightSources.Any(Function(l) l.UseDepth AndAlso l.Enabled) Then
                Avalonia.Threading.Dispatcher.UIThread.Post(Sub()
                                                                Dim ignored = EnsureLightDepthMapAsync()
                                                            End Sub)
            End If
            Return ""
        End Function

        ''' <summary>Der Schluessel der Tiefenkarte fuer den Bildstand, wie er gerade ist.</summary>
        Private Function CurrentLightDepthKey() As String
            Dim recipe = RecipeWithoutObjects()
            If recipe Is Nothing Then Return ""
            Return ImageProcessor.LightDepthKey(_currentImagePath, recipe)
        End Function

        ''' <summary>Rechnet die Tiefenkarte fuer die Lichter, wenn sie fuer diesen Bildstand fehlt,
        ''' und legt sie beim Prozessor ab. Der Schluessel haengt an Bild und Geometrie, nicht an den
        ''' Reglern: die Tiefe aendert sich nicht, wenn jemand die Belichtung zieht.</summary>
        Private Async Function EnsureLightDepthMapAsync() As Task
            If _lightDepthRunning OrElse Not DepthMapService.Available OrElse String.IsNullOrWhiteSpace(_currentImagePath) Then Return
            Dim recipe = RecipeWithoutObjects()
            If recipe Is Nothing Then Return
            recipe.LightSources = New List(Of LightSource)()
            Dim key = ImageProcessor.LightDepthKey(_currentImagePath, recipe)
            If key.Length = 0 Then Return
            If String.Equals(key, _lightDepthToken, StringComparison.Ordinal) AndAlso ImageProcessor.HasLightDepthMap(key) Then Return
            _lightDepthRunning = True
            StatusText = LocalizationService.T("Tiefe wird berechnet…")
            Dim sourcePath = RenderSourcePath
            Dim workingImage = CloneWorkingFullForRender()
            Dim stale = False
            Try
                Dim map = Await Task.Run(Function()
                                             Using rendered = ImageProcessor.RenderDisplayImage(sourcePath, recipe, workingImage)
                                                 If rendered Is Nothing Then Return Nothing
                                                 Return DepthMapService.Compute(rendered)
                                             End Using
                                         End Function)
                ' Waehrend gerechnet wurde, kann sich der Bildstand geaendert haben: ein anderes Bild,
                ' ein neuer Zuschnitt, eine Drehung, eine Retusche. Verglichen wird deshalb der ganze
                ' Schluessel, nicht nur der Pfad. Die Karte gehoert dann nirgendwohin mehr und wird
                ' verworfen; fuer den neuen Stand wird unten neu gerechnet, sobald diese Rechnung
                ' den Weg freigibt.
                If Not String.Equals(key, CurrentLightDepthKey(), StringComparison.Ordinal) Then
                    stale = True
                    map?.Dispose()
                    Return
                End If
                If map Is Nothing Then
                    _lightDepthFailedKey = key
                    StatusText = LocalizationService.T("Tiefe konnte nicht berechnet werden")
                    Return
                End If
                Using map
                    ImageProcessor.SetLightDepthMap(key, map)
                End Using
                _lightDepthToken = key
                StatusText = ""
                SchedulePreviewUpdate()
            Finally
                _lightDepthRunning = False
                If stale AndAlso _lightSources.Any(Function(l) l.UseDepth AndAlso l.Enabled) Then
                    Dim ignored = EnsureLightDepthMapAsync()
                End If
            End Try
        End Function

        ' ── Ziehen auf der Buehne ────────────────────────────────────────────────

        ''' <summary>Beginn eines Zuges an einem Griff. Der Schritt in der Historie entsteht erst mit
        ''' der ersten Bewegung: ein Klick ohne Weg hinterliesse sonst einen Schritt, der nichts
        ''' zuruecknimmt.</summary>
        ''' <param name="captureUndo">False, wenn der Zug ein eben angelegtes Licht weiterzieht: das
        ''' Anlegen ist schon der Schritt, und ein Rueckgaengig soll das Licht ganz wegnehmen statt es
        ''' erst an die Klickstelle zurueckzusetzen.</param>
        Friend Sub BeginLightEdit(id As String, Optional captureUndo As Boolean = True)
            If _lightSources.All(Function(l) l.Id <> id) Then Return
            If _selectedLightId <> id Then
                _selectedLightId = id
                RebuildLightRows()
                RaiseLightProperties()
            End If
            _lightEditUndoPending = captureUndo
            _lightEditActive = True
        End Sub

        ''' <summary>Setzt Lage und Form des Lichts aus einem Zug auf der Buehne.</summary>
        Friend Sub UpdateLightGeometry(id As String, xPercent As Double, yPercent As Double, radiusPercent As Double,
                                       angleDegrees As Double, coneDegrees As Double, aspectPercent As Double)
            Dim light = _lightSources.FirstOrDefault(Function(l) l.Id = id)
            If light Is Nothing Then Return
            If _lightEditUndoPending Then
                _lightEditUndoPending = False
                CaptureUndoState("LightMove")
            End If
            light.XPercent = Math.Max(-50.0, Math.Min(150.0, xPercent))
            light.YPercent = Math.Max(-50.0, Math.Min(150.0, yPercent))
            light.RadiusPercent = Math.Max(1.0, Math.Min(300.0, radiusPercent))
            light.AngleDegrees = NormalizeLightAngle(angleDegrees)
            light.ConeDegrees = Math.Max(2.0, Math.Min(170.0, coneDegrees))
            light.AspectPercent = Math.Max(10.0, Math.Min(1000.0, aspectPercent))
            AfterLightChanged(raiseAll:=False)
            For Each name In {NameOf(LightX), NameOf(LightY), NameOf(LightRadius), NameOf(LightAngle), NameOf(LightCone), NameOf(LightAspect)}
                Me.RaisePropertyChanged(name)
            Next
        End Sub

        Friend Sub EndLightEdit()
            _lightEditActive = False
            _lightEditUndoPending = False
        End Sub

        ''' <summary>Laedt die Lichter aus einem Rezept (Bild oeffnen, Rueckgaengig).</summary>
        Private Sub LoadLightSourcesFrom(adj As ImageAdjustments)
            _lightSources.Clear()
            If adj?.LightSources IsNot Nothing Then
                For Each l In adj.LightSources
                    If l IsNot Nothing Then _lightSources.Add(l.Clone())
                Next
            End If
            If _lightSources.All(Function(l) l.Id <> _selectedLightId) Then
                _selectedLightId = If(_lightSources.Count > 0, _lightSources(_lightSources.Count - 1).Id, "")
            End If
            _lightOverlayVersion += 1
            Me.RaisePropertyChanged(NameOf(LightOverlayVersion))
            RebuildLightRows()
            RaiseLightProperties()
            If _lightSources.Any(Function(l) l.UseDepth AndAlso l.Enabled) Then
                Dim ignored = EnsureLightDepthMapAsync()
            End If
        End Sub

        Private Function LightHistoryLabel(propertyName As String) As String
            If String.IsNullOrEmpty(propertyName) OrElse Not propertyName.StartsWith("Light", StringComparison.Ordinal) Then Return Nothing
            Select Case propertyName
                Case "LightAdd" : Return CombineHistoryLabel("Beleuchtung", "Licht hinzugefügt")
                Case "LightRemove" : Return CombineHistoryLabel("Beleuchtung", "Licht entfernt")
                Case "LightMove" : Return CombineHistoryLabel("Beleuchtung", "Licht ausgerichtet")
                Case "LightEnabled" : Return CombineHistoryLabel("Beleuchtung", "Licht ein oder aus")
                Case NameOf(LightIntensity) : Return CombineHistoryLabel("Beleuchtung", "Leuchtkraft")
                Case NameOf(LightRadius) : Return CombineHistoryLabel("Beleuchtung", "Reichweite")
                Case NameOf(LightPresetLabel) : Return CombineHistoryLabel("Beleuchtung", "Lichtart")
                Case NameOf(LightTemperature), NameOf(LightColorValue), NameOf(LightUseTemperature) : Return CombineHistoryLabel("Beleuchtung", "Lichtfarbe")
                Case Else : Return LocalizationService.T("Beleuchtung")
            End Select
        End Function

    End Class

End Namespace
