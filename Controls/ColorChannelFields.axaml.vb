Imports Avalonia
Imports Avalonia.Controls
Imports Avalonia.Data
Imports Avalonia.Markup.Xaml
Imports Avalonia.Media

Namespace Controls

    ''' <summary>
    ''' Die Kanalwerte einer Farbe als Eingabefelder: Rot, Gruen, Blau und Alpha von 0 bis 255. Steht
    ''' im Aufklapper des Farbknopfs (<see cref="ColorEditor"/>) und im <see cref="ColorMixer"/> und
    ''' ist dort ueber <see cref="Color"/> an dieselbe Farbe gebunden wie Rad, Deckkraft und Hex-Feld.
    '''
    ''' <para>Ein Kanalfeld aendert nur seinen Kanal. Das Feld A ist dieselbe Deckung wie der
    ''' Deckkraftregler des Besitzers, nur in 0 bis 255 statt in Prozent; beide gehen ueber
    ''' <see cref="Color"/>, widersprechen sich also nie. Wo die Farbe keine Deckung kennt
    ''' (<see cref="ShowAlpha"/> aus), faellt das Feld A samt Spalte weg.</para>
    '''
    ''' <para>Getipptes gilt erst beim Verlassen des Feldes oder mit der Eingabetaste
    ''' (<see cref="SliderValueUpDown.DeferTextCommit"/>): wer 200 tippt, faerbt nicht erst auf 2 und
    ''' 20 um. Werte ausserhalb 0 bis 255 begrenzt das Feld selbst auf den Rand.</para>
    ''' </summary>
    Public Class ColorChannelFields
        Inherits UserControl

        Public Shared ReadOnly ColorProperty As StyledProperty(Of Color) =
            AvaloniaProperty.Register(Of ColorChannelFields, Color)(NameOf(Color), Colors.White, defaultBindingMode:=BindingMode.TwoWay)

        Public Shared ReadOnly ShowAlphaProperty As StyledProperty(Of Boolean) =
            AvaloniaProperty.Register(Of ColorChannelFields, Boolean)(NameOf(ShowAlpha), True)

        Private Shared ReadOnly FieldNames As String() = {"RedValue", "GreenValue", "BlueValue", "AlphaChannelValue"}

        Private _suppressSync As Boolean

        Public Sub New()
            AvaloniaXamlLoader.Load(Me)
            ' Gleich breite Ziffern wie an der Anzeige der Pipette: die Zahlen stehen rechtsbuendig
            ' und springen beim Durchziehen des Rads nicht hin und her.
            For Each fieldName In FieldNames
                Dim field = Me.FindControl(Of SliderValueUpDown)(fieldName)
                If field Is Nothing Then Continue For
                field.FontFeatures = New FontFeatureCollection() From {
                    New FontFeature With {.Tag = "tnum", .Value = 1}
                }
                AddHandler field.PropertyChanged, AddressOf OnFieldPropertyChanged
            Next
            UpdateFields()
        End Sub

        Public Property Color As Color
            Get
                Return GetValue(ColorProperty)
            End Get
            Set(value As Color)
                SetValue(ColorProperty, value)
            End Set
        End Property

        Public Property ShowAlpha As Boolean
            Get
                Return GetValue(ShowAlphaProperty)
            End Get
            Set(value As Boolean)
                SetValue(ShowAlphaProperty, value)
            End Set
        End Property

        Protected Overrides Sub OnPropertyChanged(change As AvaloniaPropertyChangedEventArgs)
            MyBase.OnPropertyChanged(change)
            If change.Property Is ColorProperty Then
                UpdateFields()
            ElseIf change.Property Is ShowAlphaProperty Then
                UpdateAlphaColumn()
            End If
        End Sub

        ''' Ohne Deckung verschwindet nicht nur das Feld, sondern auch die Breite seiner Spalte: sie
        ''' geht auf Auto und misst mit dem ausgeblendeten Feld null, R, G und B teilen sich den Rest.
        ''' Die Spalte selbst bleibt stehen; eine neue ColumnDefinitions-Liste mit weniger Spalten
        ''' wirft im Grid beim naechsten Messen einen Indexfehler.
        Private Sub UpdateAlphaColumn()
            Dim alphaColumn = Me.FindControl(Of Control)("AlphaColumn")
            If alphaColumn IsNot Nothing Then alphaColumn.IsVisible = ShowAlpha
            Dim grid = Me.FindControl(Of Grid)("ChannelGrid")
            If grid Is Nothing OrElse grid.ColumnDefinitions.Count < 4 Then Return
            grid.ColumnDefinitions(3).Width = If(ShowAlpha, New GridLength(1, GridUnitType.Star), GridLength.Auto)
        End Sub

        Private Sub UpdateFields()
            If _suppressSync Then Return
            _suppressSync = True
            Try
                Dim c = Color
                SetField("RedValue", c.R)
                SetField("GreenValue", c.G)
                SetField("BlueValue", c.B)
                SetField("AlphaChannelValue", c.A)
            Finally
                _suppressSync = False
            End Try
        End Sub

        Private Sub SetField(fieldName As String, channel As Byte)
            Dim field = Me.FindControl(Of SliderValueUpDown)(fieldName)
            If field Is Nothing Then Return
            Dim target = CDec(channel)
            If field.Value.HasValue AndAlso field.Value.Value = target Then Return
            field.Value = target
        End Sub

        Private Sub OnFieldPropertyChanged(sender As Object, e As AvaloniaPropertyChangedEventArgs)
            If _suppressSync OrElse e.Property IsNot NumericUpDown.ValueProperty Then Return
            Dim field = TryCast(sender, SliderValueUpDown)
            If field Is Nothing OrElse Not field.Value.HasValue Then Return

            ' Begrenzt wird hier noch einmal, auch wenn das Feld selbst schon klemmt: ein Wert, der
            ' von aussen an Value vorbei ankaeme, darf beim Umwandeln in ein Byte nicht werfen.
            Dim channel = CByte(Math.Max(0D, Math.Min(255D, Math.Round(field.Value.Value))))
            Dim c = Color
            Dim updated As Color
            Select Case field.Name
                Case "RedValue" : updated = Avalonia.Media.Color.FromArgb(c.A, channel, c.G, c.B)
                Case "GreenValue" : updated = Avalonia.Media.Color.FromArgb(c.A, c.R, channel, c.B)
                Case "BlueValue" : updated = Avalonia.Media.Color.FromArgb(c.A, c.R, c.G, channel)
                Case "AlphaChannelValue" : updated = Avalonia.Media.Color.FromArgb(channel, c.R, c.G, c.B)
                Case Else : Return
            End Select
            If updated = c Then Return
            Color = updated
        End Sub

    End Class

End Namespace
