Imports Avalonia
Imports Avalonia.Controls
Imports Avalonia.Data
Imports Avalonia.Input
Imports Avalonia.Interactivity
Imports Avalonia.Markup.Xaml
Imports Avalonia.Media
Imports FerrumPix.ViewModels

Namespace Controls

    ''' <summary>
    ''' Farbmischer für die Objektwerkzeuge: ein Farbrad (siehe ColorWheel) und darüber die Farben,
    ''' die ein Objekt trägt - Füllung, Kontur und, bei Verläufen, die zweite Füllfarbe. Angeklickt
    ''' wird die Farbe, die das Rad gerade bearbeitet; Füllung und Kontur lassen sich tauschen.
    '''
    ''' Die Beschriftungen kommen von außen, weil dieselben Eigenschaften je nach Objekttyp anders
    ''' heißen: beim QR-Code ist die Füllfarbe der Hintergrund und die Konturfarbe der Vordergrund
    ''' (siehe ImageProcessor.ApplyAnnotations - dort ist StrokeColor die Modulfarbe).
    ''' </summary>
    Public Class ColorMixer
        Inherits UserControl

        Public Shared ReadOnly FillColorProperty As StyledProperty(Of Color) =
            AvaloniaProperty.Register(Of ColorMixer, Color)(NameOf(FillColor), Colors.White, defaultBindingMode:=BindingMode.TwoWay)

        Public Shared ReadOnly StrokeColorProperty As StyledProperty(Of Color) =
            AvaloniaProperty.Register(Of ColorMixer, Color)(NameOf(StrokeColor), Colors.Black, defaultBindingMode:=BindingMode.TwoWay)

        Public Shared ReadOnly SecondaryFillColorProperty As StyledProperty(Of Color) =
            AvaloniaProperty.Register(Of ColorMixer, Color)(NameOf(SecondaryFillColor), Colors.White, defaultBindingMode:=BindingMode.TwoWay)

        Public Shared ReadOnly FillLabelProperty As StyledProperty(Of String) =
            AvaloniaProperty.Register(Of ColorMixer, String)(NameOf(FillLabel), "Füllung")

        Public Shared ReadOnly StrokeLabelProperty As StyledProperty(Of String) =
            AvaloniaProperty.Register(Of ColorMixer, String)(NameOf(StrokeLabel), "Kontur")

        Public Shared ReadOnly SecondaryFillLabelProperty As StyledProperty(Of String) =
            AvaloniaProperty.Register(Of ColorMixer, String)(NameOf(SecondaryFillLabel), "Verlauf bis")

        Public Shared ReadOnly ShowFillProperty As StyledProperty(Of Boolean) =
            AvaloniaProperty.Register(Of ColorMixer, Boolean)(NameOf(ShowFill), True)

        Public Shared ReadOnly ShowSecondaryFillProperty As StyledProperty(Of Boolean) =
            AvaloniaProperty.Register(Of ColorMixer, Boolean)(NameOf(ShowSecondaryFill), False)

        Private Enum Slot
            Fill
            Stroke
            SecondaryFill
        End Enum

        Private _activeSlot As Slot = Slot.Fill
        Private _suppressSync As Boolean
        Private _observedVm As EditorViewModel
        Private _isAttached As Boolean

        Public Sub New()
            AvaloniaXamlLoader.Load(Me)

            Dim wheel = Me.FindControl(Of ColorWheel)("Wheel")
            If wheel IsNot Nothing Then
                AddHandler wheel.PropertyChanged, AddressOf OnWheelPropertyChanged
                ' Erst beim Loslassen in die geteilte Liste der zuletzt benutzten Farben - beim Ziehen
                ' meldet das Rad jede Mausbewegung, das würde die zehn Plätze mit Zwischentönen fluten.
                AddHandler wheel.PointerReleased, AddressOf OnWheelPointerReleased
            End If

            Dim alphaSlider = Me.FindControl(Of RoundSlider)("AlphaSlider")
            If alphaSlider IsNot Nothing Then AddHandler alphaSlider.PropertyChanged, AddressOf OnAlphaPropertyChanged

            Dim alphaValue = Me.FindControl(Of SliderValueUpDown)("AlphaValue")
            If alphaValue IsNot Nothing Then AddHandler alphaValue.PropertyChanged, AddressOf OnAlphaPropertyChanged

            Dim hexBox = Me.FindControl(Of TextBox)("HexTextBox")
            If hexBox IsNot Nothing Then AddHandler hexBox.LostFocus, AddressOf OnHexLostFocus

            AddHandler Me.DataContextChanged, AddressOf OnOwnDataContextChanged
            UpdateVisuals()
        End Sub

        Public Property FillColor As Color
            Get
                Return GetValue(FillColorProperty)
            End Get
            Set(value As Color)
                SetValue(FillColorProperty, value)
            End Set
        End Property

        Public Property StrokeColor As Color
            Get
                Return GetValue(StrokeColorProperty)
            End Get
            Set(value As Color)
                SetValue(StrokeColorProperty, value)
            End Set
        End Property

        Public Property SecondaryFillColor As Color
            Get
                Return GetValue(SecondaryFillColorProperty)
            End Get
            Set(value As Color)
                SetValue(SecondaryFillColorProperty, value)
            End Set
        End Property

        Public Property FillLabel As String
            Get
                Return GetValue(FillLabelProperty)
            End Get
            Set(value As String)
                SetValue(FillLabelProperty, value)
            End Set
        End Property

        Public Property StrokeLabel As String
            Get
                Return GetValue(StrokeLabelProperty)
            End Get
            Set(value As String)
                SetValue(StrokeLabelProperty, value)
            End Set
        End Property

        Public Property SecondaryFillLabel As String
            Get
                Return GetValue(SecondaryFillLabelProperty)
            End Get
            Set(value As String)
                SetValue(SecondaryFillLabelProperty, value)
            End Set
        End Property

        Public Property ShowFill As Boolean
            Get
                Return GetValue(ShowFillProperty)
            End Get
            Set(value As Boolean)
                SetValue(ShowFillProperty, value)
            End Set
        End Property

        Public Property ShowSecondaryFill As Boolean
            Get
                Return GetValue(ShowSecondaryFillProperty)
            End Get
            Set(value As Boolean)
                SetValue(ShowSecondaryFillProperty, value)
            End Set
        End Property

        Protected Overrides Sub OnPropertyChanged(change As AvaloniaPropertyChangedEventArgs)
            MyBase.OnPropertyChanged(change)
            If change.Property Is FillColorProperty OrElse
               change.Property Is StrokeColorProperty OrElse
               change.Property Is SecondaryFillColorProperty OrElse
               change.Property Is FillLabelProperty OrElse
               change.Property Is StrokeLabelProperty OrElse
               change.Property Is SecondaryFillLabelProperty OrElse
               change.Property Is ShowFillProperty OrElse
               change.Property Is ShowSecondaryFillProperty Then
                UpdateVisuals()
            End If
        End Sub

        ' ---- aktive Farbe ----

        Private Property ActiveColor As Color
            Get
                Select Case _activeSlot
                    Case Slot.Stroke : Return StrokeColor
                    Case Slot.SecondaryFill : Return SecondaryFillColor
                    Case Else : Return FillColor
                End Select
            End Get
            Set(value As Color)
                Select Case _activeSlot
                    Case Slot.Stroke : StrokeColor = value
                    Case Slot.SecondaryFill : SecondaryFillColor = value
                    Case Else : FillColor = value
                End Select
            End Set
        End Property

        Private Function IsSlotVisible(slot As Slot) As Boolean
            Select Case slot
                Case Slot.Fill : Return ShowFill
                Case Slot.SecondaryFill : Return ShowSecondaryFill
                Case Else : Return True
            End Select
        End Function

        Private Sub SetActiveSlot(slot As Slot)
            If Not IsSlotVisible(slot) Then Return
            _activeSlot = slot
            UpdateVisuals()
        End Sub

        Private Sub UpdateVisuals()
            If _suppressSync Then Return
            _suppressSync = True
            Try
                ' Bildobjekte haben keine Füllung, und ohne Verlauf gibt es keine zweite Füllfarbe -
                ' wird das aktive Feld dabei unsichtbar, springt die Auswahl auf die Kontur.
                If Not IsSlotVisible(_activeSlot) Then _activeSlot = Slot.Stroke

                SetSwatch("FillSwatch", FillColor, _activeSlot = Slot.Fill, ShowFill, FillLabel)
                SetSwatch("StrokeSwatch", StrokeColor, _activeSlot = Slot.Stroke, True, StrokeLabel)
                SetSwatch("SecondarySwatch", SecondaryFillColor, _activeSlot = Slot.SecondaryFill, ShowSecondaryFill, SecondaryFillLabel)

                Dim swapButton = Me.FindControl(Of Button)("SwapButton")
                If swapButton IsNot Nothing Then
                    swapButton.IsVisible = ShowFill
                    ToolTip.SetTip(swapButton, $"{FillLabel} und {StrokeLabel} tauschen")
                End If

                Dim label = Me.FindControl(Of TextBlock)("ActiveSlotLabel")
                If label IsNot Nothing Then label.Text = ActiveSlotLabelText()

                Dim active = ActiveColor

                Dim wheel = Me.FindControl(Of ColorWheel)("Wheel")
                If wheel IsNot Nothing AndAlso wheel.Color <> active Then wheel.Color = active

                Dim alphaPercent = Math.Round(active.A / 255.0 * 100.0)
                Dim alphaSlider = Me.FindControl(Of RoundSlider)("AlphaSlider")
                If alphaSlider IsNot Nothing AndAlso Math.Abs(alphaSlider.Value - alphaPercent) > 0.5 Then alphaSlider.Value = alphaPercent

                ' NumericUpDown.Value ist Decimal? - ohne die Umwandlung findet VB keine Math.Abs-Überladung.
                Dim alphaValue = Me.FindControl(Of SliderValueUpDown)("AlphaValue")
                If alphaValue IsNot Nothing AndAlso Math.Abs(CDbl(If(alphaValue.Value, 0D)) - alphaPercent) > 0.5 Then
                    alphaValue.Value = CDec(alphaPercent)
                End If

                Dim hex = FormatHex(active)
                Dim hexBox = Me.FindControl(Of TextBox)("HexTextBox")
                If hexBox IsNot Nothing AndAlso Not String.Equals(hexBox.Text, hex, StringComparison.OrdinalIgnoreCase) Then hexBox.Text = hex
            Finally
                _suppressSync = False
            End Try
        End Sub

        Private Function ActiveSlotLabelText() As String
            Select Case _activeSlot
                Case Slot.Stroke : Return StrokeLabel
                Case Slot.SecondaryFill : Return SecondaryFillLabel
                Case Else : Return FillLabel
            End Select
        End Function

        Private Sub SetSwatch(name As String, color As Color, isActive As Boolean, isVisible As Boolean, tip As String)
            Dim swatch = Me.FindControl(Of Border)(name)
            If swatch Is Nothing Then Return
            swatch.IsVisible = isVisible
            swatch.Background = New SolidColorBrush(color)
            swatch.BorderBrush = If(isActive,
                                    TryFindResourceBrush("FP.Accent", Brushes.Orange),
                                    TryFindResourceBrush("FP.Border.Strong", Brushes.Gray))
            swatch.BorderThickness = New Thickness(If(isActive, 2.5, 1.5))
            ToolTip.SetTip(swatch, tip)
        End Sub

        Private Function TryFindResourceBrush(key As String, fallback As IBrush) As IBrush
            Dim value As Object = Nothing
            If Me.TryFindResource(key, value) AndAlso TypeOf value Is IBrush Then Return DirectCast(value, IBrush)
            Return fallback
        End Function

        Private Shared Function FormatHex(c As Color) As String
            Return $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}"
        End Function

        ' ---- Ereignisse ----

        Private Sub OnWheelPropertyChanged(sender As Object, e As AvaloniaPropertyChangedEventArgs)
            If _suppressSync OrElse e.Property IsNot ColorWheel.ColorProperty Then Return
            Dim wheel = TryCast(sender, ColorWheel)
            If wheel Is Nothing Then Return
            ActiveColor = wheel.Color
        End Sub

        Private Sub OnWheelPointerReleased(sender As Object, e As PointerReleasedEventArgs)
            RegisterRecent(ActiveColor)
        End Sub

        Private Sub OnAlphaPropertyChanged(sender As Object, e As AvaloniaPropertyChangedEventArgs)
            If _suppressSync Then Return
            If e.Property.Name <> "Value" Then Return

            Dim percent As Double
            Dim slider = TryCast(sender, RoundSlider)
            If slider IsNot Nothing Then
                percent = slider.Value
            Else
                Dim upDown = TryCast(sender, SliderValueUpDown)
                If upDown Is Nothing Then Return
                percent = CDbl(If(upDown.Value, 0D))
            End If

            Dim alpha = CByte(Math.Max(0, Math.Min(255, Math.Round(percent / 100.0 * 255.0))))
            Dim c = ActiveColor
            If c.A = alpha Then Return
            ActiveColor = Color.FromArgb(alpha, c.R, c.G, c.B)
        End Sub

        Private Sub OnHexLostFocus(sender As Object, e As RoutedEventArgs)
            If _suppressSync Then Return
            Dim hexBox = TryCast(sender, TextBox)
            If hexBox Is Nothing OrElse String.IsNullOrWhiteSpace(hexBox.Text) Then Return
            Try
                Dim parsed = Color.Parse(hexBox.Text.Trim())
                ActiveColor = parsed
                RegisterRecent(parsed)
            Catch
                ' Ungültige Eingabe: der nächste UpdateVisuals-Lauf schreibt den echten Wert zurück.
                UpdateVisuals()
            End Try
        End Sub

        Private Sub OnFillSwatchPressed(sender As Object, e As PointerPressedEventArgs)
            SetActiveSlot(Slot.Fill)
            e.Handled = True
        End Sub

        Private Sub OnStrokeSwatchPressed(sender As Object, e As PointerPressedEventArgs)
            SetActiveSlot(Slot.Stroke)
            e.Handled = True
        End Sub

        Private Sub OnSecondarySwatchPressed(sender As Object, e As PointerPressedEventArgs)
            SetActiveSlot(Slot.SecondaryFill)
            e.Handled = True
        End Sub

        Private Sub OnSwapClick(sender As Object, e As RoutedEventArgs)
            If Not ShowFill Then Return
            Dim fill = FillColor
            FillColor = StrokeColor
            StrokeColor = fill
        End Sub

        ''' Der Mischer zeigt die zuletzt benutzten Farben nicht selbst an, speist sie aber weiterhin:
        ''' die Flyouts der übrigen Farbfelder (ColorPickerButton) greifen auf dieselbe Liste zu.
        Private Shared Sub RegisterRecent(c As Color)
            ColorPickerButton.SharedRecentColors.Remove(c)
            ColorPickerButton.SharedRecentColors.Insert(0, c)
            While ColorPickerButton.SharedRecentColors.Count > 10
                ColorPickerButton.SharedRecentColors.RemoveAt(ColorPickerButton.SharedRecentColors.Count - 1)
            End While
        End Sub

        ' ---- Pipette ----

        Private Sub OnOwnDataContextChanged(sender As Object, e As EventArgs)
            RebindViewModel()
        End Sub

        ''' Siehe ColorPickerButton: das EditorViewModel überlebt die EditorView, die bei jedem
        ''' Moduswechsel neu gebaut wird. Ohne Abmelden beim Entfernen aus dem Baum bliebe dieses
        ''' Control je Editor-Besuch am ViewModel hängen.
        Protected Overrides Sub OnAttachedToVisualTree(e As VisualTreeAttachmentEventArgs)
            MyBase.OnAttachedToVisualTree(e)
            _isAttached = True
            RebindViewModel()
        End Sub

        Protected Overrides Sub OnDetachedFromVisualTree(e As VisualTreeAttachmentEventArgs)
            MyBase.OnDetachedFromVisualTree(e)
            _isAttached = False
            UnsubscribeViewModel()
        End Sub

        Private Sub RebindViewModel()
            UnsubscribeViewModel()
            If Not _isAttached Then Return
            _observedVm = TryCast(DataContext, EditorViewModel)
            If _observedVm IsNot Nothing Then
                AddHandler _observedVm.PropertyChanged, AddressOf OnViewModelPropertyChanged
            End If
        End Sub

        Private Sub UnsubscribeViewModel()
            If _observedVm Is Nothing Then Return
            RemoveHandler _observedVm.PropertyChanged, AddressOf OnViewModelPropertyChanged
            _observedVm = Nothing
        End Sub

        Private Sub OnViewModelPropertyChanged(sender As Object, e As System.ComponentModel.PropertyChangedEventArgs)
            If e.PropertyName <> NameOf(EditorViewModel.IsPickingColorFromImage) Then Return
            If _observedVm Is Nothing OrElse _observedVm.IsPickingColorFromImage Then Return
            SetPickingActive(False)
        End Sub

        Private Sub SetPickingActive(active As Boolean)
            Dim button = Me.FindControl(Of Button)("EyedropperButton")
            If button Is Nothing Then Return
            If active Then
                button.Classes.Add("active")
            Else
                button.Classes.Remove("active")
            End If
        End Sub

        ''' Die aufgenommene Farbe landet in dem Feld, das gerade aktiv ist - so lässt sich auch die
        ''' Kontur direkt aus dem Bild greifen.
        Private Sub OnEyedropperClick(sender As Object, e As RoutedEventArgs)
            Dim vm = TryCast(DataContext, EditorViewModel)
            If vm Is Nothing Then Return
            SetPickingActive(True)
            vm.BeginColorPick(Sub(picked As Color)
                                  ActiveColor = picked
                                  RegisterRecent(picked)
                              End Sub)
        End Sub

    End Class

End Namespace
