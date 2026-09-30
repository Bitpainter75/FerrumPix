Imports System.Collections.ObjectModel
Imports Avalonia
Imports Avalonia.Controls
Imports FerrumPix.Services
Imports Avalonia.Data
Imports Avalonia.Interactivity
Imports Avalonia.Markup.Xaml
Imports Avalonia.Media
Imports FerrumPix.ViewModels

Namespace Controls

    ''' Zentraler, wiederverwendbarer Farbwähler:
    ''' Farbfeld+Hex-Button mit Flyout (Farbrad, Hex-Eingabe, zuletzt verwendete Farben) sowie eine
    ''' eingebaute Pipette, die die Farbe direkt aus dem bearbeiteten Bild aufnimmt. Ersetzt die vormals
    ''' pro Eigenschaften-Panel einzeln kopierten Button+Flyout+ColorPicker-Blöcke - einfach per
    ''' SelectedColor="{Binding ...Value, Mode=TwoWay}" verwenden.
    Public Class ColorPickerButton
        Inherits UserControl

        ''' <summary>Ein Flyout entsteht erst beim Oeffnen aus seinem Template und ist beim
        ''' einmaligen Durchlauf ueber das Fenster noch nicht da. Ohne diesen Einstieg bleibt sein
        ''' Inhalt in der Ausgangssprache stehen, waehrend die uebrige Oberflaeche umgeschaltet
        ''' hat.</summary>
        Private Sub OnLocalizedFlyoutOpened(sender As Object, e As EventArgs)
            Dim content = TryCast(TryCast(sender, Flyout)?.Content, Avalonia.LogicalTree.ILogical)
            If content IsNot Nothing Then LocalizationService.ApplyTo(content)
            Me.FindControl(Of ColorEditor)("Editor")?.MarkOriginal()
        End Sub

        Public Shared ReadOnly SelectedColorProperty As StyledProperty(Of Color) =
            AvaloniaProperty.Register(Of ColorPickerButton, Color)(NameOf(SelectedColor), Colors.White, defaultBindingMode:=BindingMode.TwoWay)

        ''' <summary>Die Pipette nimmt aus dem Bild im Editor auf. Ausserhalb davon (Einstellungen,
        ''' Dialoge ohne Bild) gibt es nichts aufzunehmen, dort wird sie ausgeblendet.</summary>
        Public Shared ReadOnly ShowEyedropperProperty As StyledProperty(Of Boolean) =
            AvaloniaProperty.Register(Of ColorPickerButton, Boolean)(NameOf(ShowEyedropper), True)

        ' Zuletzt verwendete Farben werden STATISCH (über alle Instanzen hinweg) geteilt, damit die
        ' Pipette/Farbrad-Auswahl in einem Panel auch im Flyout eines anderen Farbfelds auftaucht -
        ' genau das bisherige Verhalten von EditorViewModel.RecentColors, nur nicht mehr an eine
        ' bestimmte ViewModel-Instanz gebunden, damit dieses Control eigenständig wiederverwendbar bleibt.
        Public Shared ReadOnly SharedRecentColors As New ObservableCollection(Of Color)()

        Private _suppressSync As Boolean
        Private _isPicking As Boolean
        Private _observedVm As EditorViewModel
        Private _isAttached As Boolean

        Public Sub New()
            AvaloniaXamlLoader.Load(Me)

            Dim editor = Me.FindControl(Of ColorEditor)("Editor")
            If editor IsNot Nothing Then AddHandler editor.PropertyChanged, AddressOf OnEditorPropertyChanged

            UpdateVisuals()

            AddHandler Me.DataContextChanged, AddressOf OnOwnDataContextChanged
        End Sub

        ''' Hängt sich an das PropertyChanged des jeweiligen EditorViewModel, um die Akzentfarbe
        ''' am Pipetten-Icon auszuschalten, sobald das Picking endet - egal ob durch erfolgreiche
        ''' Aufnahme oder durch Abbruch (Escape), da beide Wege IsPickingColorFromImage auf False setzen.
        Private Sub OnOwnDataContextChanged(sender As Object, e As EventArgs)
            RebindViewModel()
        End Sub

        ''' Das EditorViewModel lebt über die ganze Sitzung, die EditorView wird bei jedem Moduswechsel
        ''' neu gebaut (ViewLocator). Ohne Abmelden beim Entfernen aus dem Baum bliebe dieses Control samt
        ''' seinem View-Baum über das Abo am ViewModel hängen - einmal je Editor-Besuch. DataContextChanged
        ''' allein genügt dafür nicht: beim Verwerfen der View feuert es nicht.
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

        Public Property ShowEyedropper As Boolean
            Get
                Return GetValue(ShowEyedropperProperty)
            End Get
            Set(value As Boolean)
                SetValue(ShowEyedropperProperty, value)
            End Set
        End Property

        Protected Overrides Sub OnPropertyChanged(change As AvaloniaPropertyChangedEventArgs)
            MyBase.OnPropertyChanged(change)
            If change.Property = SelectedColorProperty Then
                UpdateVisuals()
            ElseIf change.Property = ShowEyedropperProperty Then
                Dim eyedropperButton = Me.FindControl(Of Button)("EyedropperButton")
                If eyedropperButton IsNot Nothing Then eyedropperButton.IsVisible = ShowEyedropper
            End If
        End Sub

        Private Sub UpdateVisuals()
            Dim swatch = Me.FindControl(Of Border)("SwatchBorder")
            If swatch IsNot Nothing Then swatch.Background = New SolidColorBrush(SelectedColor)

            Dim hexText = Me.FindControl(Of TextBlock)("HexTextBlock")
            If hexText IsNot Nothing Then hexText.Text = FormatHex(SelectedColor)

            If _suppressSync Then Return
            _suppressSync = True
            Try
                Dim editor = Me.FindControl(Of ColorEditor)("Editor")
                If editor IsNot Nothing AndAlso editor.Color <> SelectedColor Then editor.Color = SelectedColor
            Finally
                _suppressSync = False
            End Try
        End Sub

        Private Shared Function FormatHex(c As Color) As String
            Return $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}"
        End Function

        Private Sub OnEditorPropertyChanged(sender As Object, e As AvaloniaPropertyChangedEventArgs)
            If _suppressSync OrElse e.Property IsNot ColorEditor.ColorProperty Then Return
            Dim editor = TryCast(sender, ColorEditor)
            If editor Is Nothing Then Return
            SelectedColor = editor.Color
        End Sub

        ''' Geteilt mit ColorEditor: Rad, Hex-Feld und Pipette tragen hier ein.
        Friend Shared Sub RegisterRecent(c As Color)
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
