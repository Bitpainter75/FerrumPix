Imports System.Collections.ObjectModel
Imports Avalonia
Imports Avalonia.Controls
Imports Avalonia.Data
Imports Avalonia.Interactivity
Imports Avalonia.Markup.Xaml
Imports Avalonia.Media
Imports FerrumPix.ViewModels

Namespace Controls

    ''' Zentraler, wiederverwendbarer Farbwähler (Phase 1 Roadmap "Zentraler Farbregler + Farbpipette"):
    ''' Farbfeld+Hex-Button mit Flyout (Farbrad, Hex-Eingabe, zuletzt verwendete Farben) sowie eine
    ''' eingebaute Pipette, die die Farbe direkt aus dem bearbeiteten Bild aufnimmt. Ersetzt die vormals
    ''' pro Eigenschaften-Panel einzeln kopierten Button+Flyout+ColorPicker-Blöcke - einfach per
    ''' SelectedColor="{Binding ...Value, Mode=TwoWay}" verwenden.
    Public Class ColorPickerButton
        Inherits UserControl

        Public Shared ReadOnly SelectedColorProperty As StyledProperty(Of Color) =
            AvaloniaProperty.Register(Of ColorPickerButton, Color)(NameOf(SelectedColor), Colors.White, defaultBindingMode:=BindingMode.TwoWay)

        ' Zuletzt verwendete Farben werden STATISCH (über alle Instanzen hinweg) geteilt, damit die
        ' Pipette/Farbrad-Auswahl in einem Panel auch im Flyout eines anderen Farbfelds auftaucht -
        ' genau das bisherige Verhalten von EditorViewModel.RecentColors, nur nicht mehr an eine
        ' bestimmte ViewModel-Instanz gebunden, damit dieses Control eigenständig wiederverwendbar bleibt.
        Public Shared ReadOnly SharedRecentColors As New ObservableCollection(Of Color)()

        Private _suppressSync As Boolean
        Private _isPicking As Boolean
        Private _observedVm As EditorViewModel

        Public Sub New()
            AvaloniaXamlLoader.Load(Me)

            Dim recentList = Me.FindControl(Of ItemsControl)("RecentColorsList")
            If recentList IsNot Nothing Then recentList.ItemsSource = SharedRecentColors

            Dim colorPicker = Me.FindControl(Of ColorPicker)("InnerColorPicker")
            If colorPicker IsNot Nothing Then
                AddHandler colorPicker.ColorChanged, AddressOf OnInnerColorPickerChanged
            End If

            Dim hexBox = Me.FindControl(Of TextBox)("HexTextBox")
            If hexBox IsNot Nothing Then
                AddHandler hexBox.LostFocus, AddressOf OnHexTextBoxLostFocus
            End If

            UpdateVisuals()

            AddHandler Me.DataContextChanged, AddressOf OnOwnDataContextChanged
        End Sub

        ''' Hängt sich an das PropertyChanged des jeweiligen EditorViewModel, um die Akzentfarbe
        ''' am Pipetten-Icon auszuschalten, sobald das Picking endet - egal ob durch erfolgreiche
        ''' Aufnahme oder durch Abbruch (Escape), da beide Wege IsPickingColorFromImage auf False setzen.
        Private Sub OnOwnDataContextChanged(sender As Object, e As EventArgs)
            If _observedVm IsNot Nothing Then
                RemoveHandler _observedVm.PropertyChanged, AddressOf OnViewModelPropertyChanged
            End If
            _observedVm = TryCast(DataContext, EditorViewModel)
            If _observedVm IsNot Nothing Then
                AddHandler _observedVm.PropertyChanged, AddressOf OnViewModelPropertyChanged
            End If
        End Sub

        Private Sub OnViewModelPropertyChanged(sender As Object, e As System.ComponentModel.PropertyChangedEventArgs)
            If e.PropertyName <> NameOf(EditorViewModel.IsPickingColorFromImage) Then Return
            If _observedVm Is Nothing OrElse _observedVm.IsPickingColorFromImage Then Return
            SetPickingActive(False)
        End Sub

        Private Sub SetPickingActive(active As Boolean)
            _isPicking = active
            Dim eyedropperButton = Me.FindControl(Of Button)("EyedropperButton")
            If eyedropperButton Is Nothing Then Return
            If active Then
                eyedropperButton.Classes.Add("active")
            Else
                eyedropperButton.Classes.Remove("active")
            End If
        End Sub

        Public Property SelectedColor As Color
            Get
                Return GetValue(SelectedColorProperty)
            End Get
            Set(value As Color)
                SetValue(SelectedColorProperty, value)
            End Set
        End Property

        Protected Overrides Sub OnPropertyChanged(change As AvaloniaPropertyChangedEventArgs)
            MyBase.OnPropertyChanged(change)
            If change.Property = SelectedColorProperty Then
                UpdateVisuals()
            End If
        End Sub

        Private Sub UpdateVisuals()
            Dim swatch = Me.FindControl(Of Border)("SwatchBorder")
            If swatch IsNot Nothing Then swatch.Background = New SolidColorBrush(SelectedColor)

            Dim hex = FormatHex(SelectedColor)
            Dim hexText = Me.FindControl(Of TextBlock)("HexTextBlock")
            If hexText IsNot Nothing Then hexText.Text = hex

            If _suppressSync Then Return
            _suppressSync = True
            Try
                Dim colorPicker = Me.FindControl(Of ColorPicker)("InnerColorPicker")
                If colorPicker IsNot Nothing AndAlso colorPicker.Color <> SelectedColor Then colorPicker.Color = SelectedColor
                Dim hexBox = Me.FindControl(Of TextBox)("HexTextBox")
                If hexBox IsNot Nothing AndAlso Not String.Equals(hexBox.Text, hex, StringComparison.OrdinalIgnoreCase) Then hexBox.Text = hex
            Finally
                _suppressSync = False
            End Try
        End Sub

        Private Shared Function FormatHex(c As Color) As String
            Return $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}"
        End Function

        Private Sub OnInnerColorPickerChanged(sender As Object, e As ColorChangedEventArgs)
            If _suppressSync Then Return
            SelectedColor = e.NewColor
            RegisterRecent(e.NewColor)
        End Sub

        Private Sub OnHexTextBoxLostFocus(sender As Object, e As RoutedEventArgs)
            If _suppressSync Then Return
            Dim hexBox = TryCast(sender, TextBox)
            If hexBox Is Nothing OrElse String.IsNullOrWhiteSpace(hexBox.Text) Then Return
            Try
                Dim parsed = Color.Parse(hexBox.Text.Trim())
                SelectedColor = parsed
                RegisterRecent(parsed)
            Catch
                ' Ungültige Eingabe wird beim nächsten UpdateVisuals wieder auf den echten Wert zurückgesetzt.
                UpdateVisuals()
            End Try
        End Sub

        Private Sub OnRecentColorClick(sender As Object, e As RoutedEventArgs)
            Dim btn = TryCast(sender, Button)
            If btn Is Nothing OrElse Not TypeOf btn.Tag Is Color Then Return
            SelectedColor = CType(btn.Tag, Color)
        End Sub

        Private Shared Sub RegisterRecent(c As Color)
            SharedRecentColors.Remove(c)
            SharedRecentColors.Insert(0, c)
            While SharedRecentColors.Count > 10
                SharedRecentColors.RemoveAt(SharedRecentColors.Count - 1)
            End While
        End Sub

        ''' Pipette: merkt bei EditorViewModel an, dass der nächste Klick auf das Bild die Farbe
        ''' liefern soll - der übergebene Callback setzt sie direkt auf DIESE Control-Instanz (nicht
        ''' auf eine feste ViewModel-Eigenschaft), wodurch die Pipette für jedes Farbfeld funktioniert.
        Private Sub OnEyedropperClick(sender As Object, e As RoutedEventArgs)
            Dim vm = TryCast(DataContext, EditorViewModel)
            If vm Is Nothing Then Return
            SetPickingActive(True)
            vm.BeginColorPick(Sub(pickedColor As Color)
                                   SelectedColor = pickedColor
                                   RegisterRecent(pickedColor)
                               End Sub)
        End Sub

    End Class

End Namespace
