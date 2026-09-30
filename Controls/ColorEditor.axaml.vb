Imports Avalonia
Imports Avalonia.Controls
Imports Avalonia.Data
Imports Avalonia.Input
Imports Avalonia.Interactivity
Imports Avalonia.Markup.Xaml
Imports Avalonia.Media

Namespace Controls

    ''' <summary>
    ''' Der Inhalt eines Farb-Aufklappers: Farbrad (siehe ColorWheel), Deckkraft, Hex-Wert und die
    ''' zuletzt benutzten Farben. Ersetzt Avalonias ColorPicker, damit jeder Farbwaehler der
    ''' Anwendung genauso aussieht und bedient wird wie der Farbmischer der Objektwerkzeuge.
    '''
    ''' Die Vergleichsfarbe links neben dem Hex-Feld ist die Farbe beim Oeffnen. Der Besitzer setzt
    ''' sie ueber <see cref="MarkOriginal"/>, sobald sein Aufklapper aufgeht.
    ''' </summary>
    Public Class ColorEditor
        Inherits UserControl

        Public Shared ReadOnly ColorProperty As StyledProperty(Of Color) =
            AvaloniaProperty.Register(Of ColorEditor, Color)(NameOf(Color), Colors.White, defaultBindingMode:=BindingMode.TwoWay)

        Private _suppressSync As Boolean
        Private _original As Color = Colors.White

        Public Sub New()
            AvaloniaXamlLoader.Load(Me)

            Dim recentList = Me.FindControl(Of ItemsControl)("RecentColorsList")
            If recentList IsNot Nothing Then recentList.ItemsSource = ColorPickerButton.SharedRecentColors

            Dim wheel = Me.FindControl(Of ColorWheel)("Wheel")
            If wheel IsNot Nothing Then
                AddHandler wheel.PropertyChanged, AddressOf OnWheelPropertyChanged
                ' Erst beim Loslassen in die Liste der zuletzt benutzten Farben - beim Ziehen meldet
                ' das Rad jede Mausbewegung, das wuerde die zehn Plaetze mit Zwischentoenen fluten.
                AddHandler wheel.PointerReleased, AddressOf OnWheelPointerReleased
            End If

            Dim alphaSlider = Me.FindControl(Of RoundSlider)("AlphaSlider")
            If alphaSlider IsNot Nothing Then AddHandler alphaSlider.PropertyChanged, AddressOf OnAlphaPropertyChanged

            Dim alphaValue = Me.FindControl(Of SliderValueUpDown)("AlphaValue")
            If alphaValue IsNot Nothing Then AddHandler alphaValue.PropertyChanged, AddressOf OnAlphaPropertyChanged

            Dim hexBox = Me.FindControl(Of TextBox)("HexTextBox")
            If hexBox IsNot Nothing Then
                AddHandler hexBox.LostFocus, AddressOf OnHexLostFocus
                AddHandler hexBox.KeyDown, AddressOf OnHexKeyDown
            End If

            UpdateVisuals()
        End Sub

        Public Property Color As Color
            Get
                Return GetValue(ColorProperty)
            End Get
            Set(value As Color)
                SetValue(ColorProperty, value)
            End Set
        End Property

        ''' <summary>Haelt die aktuelle Farbe als Vergleich fest. Aufgerufen beim Oeffnen des
        ''' Aufklappers, damit links steht, womit man angefangen hat.</summary>
        Public Sub MarkOriginal()
            _original = Color
            UpdateVisuals()
        End Sub

        Protected Overrides Sub OnPropertyChanged(change As AvaloniaPropertyChangedEventArgs)
            MyBase.OnPropertyChanged(change)
            If change.Property Is ColorProperty Then UpdateVisuals()
        End Sub

        Private Sub UpdateVisuals()
            If _suppressSync Then Return
            _suppressSync = True
            Try
                Dim c = Color

                Dim wheel = Me.FindControl(Of ColorWheel)("Wheel")
                If wheel IsNot Nothing AndAlso wheel.Color <> c Then wheel.Color = c

                Dim alphaPercent = Math.Round(c.A / 255.0 * 100.0)
                Dim alphaSlider = Me.FindControl(Of RoundSlider)("AlphaSlider")
                If alphaSlider IsNot Nothing AndAlso Math.Abs(alphaSlider.Value - alphaPercent) > 0.5 Then alphaSlider.Value = alphaPercent

                ' NumericUpDown.Value ist Decimal? - ohne die Umwandlung findet VB keine Math.Abs-Ueberladung.
                Dim alphaValue = Me.FindControl(Of SliderValueUpDown)("AlphaValue")
                If alphaValue IsNot Nothing AndAlso Math.Abs(CDbl(If(alphaValue.Value, 0D)) - alphaPercent) > 0.5 Then
                    alphaValue.Value = CDec(alphaPercent)
                End If

                Dim hex = FormatHex(c)
                Dim hexBox = Me.FindControl(Of TextBox)("HexTextBox")
                If hexBox IsNot Nothing AndAlso Not hexBox.IsFocused AndAlso
                   Not String.Equals(hexBox.Text, hex, StringComparison.OrdinalIgnoreCase) Then hexBox.Text = hex

                Dim current = Me.FindControl(Of Border)("CurrentSwatch")
                If current IsNot Nothing Then current.Background = New SolidColorBrush(c)
                Dim original = Me.FindControl(Of Border)("OriginalSwatch")
                If original IsNot Nothing Then
                    original.Background = New SolidColorBrush(_original)
                    ToolTip.SetTip(original, FormatHex(_original))
                End If
            Finally
                _suppressSync = False
            End Try
        End Sub

        Private Shared Function FormatHex(c As Color) As String
            Return $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}"
        End Function

        ' ---- Ereignisse ----

        Private Sub OnWheelPropertyChanged(sender As Object, e As AvaloniaPropertyChangedEventArgs)
            If _suppressSync OrElse e.Property IsNot ColorWheel.ColorProperty Then Return
            Dim wheel = TryCast(sender, ColorWheel)
            If wheel Is Nothing Then Return
            Color = wheel.Color
        End Sub

        Private Sub OnWheelPointerReleased(sender As Object, e As PointerReleasedEventArgs)
            ColorPickerButton.RegisterRecent(Color)
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
            Dim c = Color
            If c.A = alpha Then Return
            Color = Avalonia.Media.Color.FromArgb(alpha, c.R, c.G, c.B)
        End Sub

        Private Sub OnHexLostFocus(sender As Object, e As RoutedEventArgs)
            CommitHex(TryCast(sender, TextBox))
        End Sub

        Private Sub OnHexKeyDown(sender As Object, e As KeyEventArgs)
            If e.Key <> Key.Enter Then Return
            CommitHex(TryCast(sender, TextBox))
            e.Handled = True
        End Sub

        ''' Akzeptiert alles, was Color.Parse kennt (#RGB, #RRGGBB, #AARRGGBB, Farbnamen). Eine
        ''' ungueltige Eingabe wird durch den echten Wert ersetzt.
        Private Sub CommitHex(hexBox As TextBox)
            If _suppressSync OrElse hexBox Is Nothing Then Return
            Dim text = If(hexBox.Text, "").Trim()
            Dim parsed As Color
            If text.Length > 0 AndAlso Avalonia.Media.Color.TryParse(text, parsed) Then
                Color = parsed
                ColorPickerButton.RegisterRecent(parsed)
            End If
            hexBox.Text = FormatHex(Color)
        End Sub

        Private Sub OnRecentColorClick(sender As Object, e As RoutedEventArgs)
            Dim btn = TryCast(sender, Button)
            If btn Is Nothing OrElse Not TypeOf btn.Tag Is Color Then Return
            Color = CType(btn.Tag, Color)
        End Sub

        Private Sub OnOriginalSwatchPressed(sender As Object, e As PointerPressedEventArgs)
            Color = _original
            e.Handled = True
        End Sub

    End Class

End Namespace
