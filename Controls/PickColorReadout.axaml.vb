Imports Avalonia
Imports Avalonia.Controls
Imports Avalonia.Markup.Xaml
Imports Avalonia.Media
Imports FerrumPix.Services

Namespace Controls

    ''' <summary>Die Anzeige an einer Pipette: Farbfeld und Wert der Farbe unter der Spitze.
    '''
    ''' <para>EIN STEUERELEMENT FUER ALLE PIPETTEN. Die Ansicht legt es in den Canvas, ueber dem die
    ''' Pipette schwebt, und ruft bei jeder Bewegung <see cref="ShowAt"/> mit der Farbe, die DIESE
    ''' Pipette beim Klick uebernaehme. Was dort steht (Hex, R G B oder beides), kommt aus der
    ''' Einstellung PickerReadout; die Textbildung liegt in
    ''' <see cref="PickerReadoutFormatter.FormatPickerReadout"/>.</para>
    '''
    ''' <para>Sie sitzt rechts unter dem Zeiger, weil das Pipettensymbol nach rechts oben ragt, und
    ''' klappt an den Raendern des Canvas auf die andere Seite, statt abgeschnitten zu werden. Ohne
    ''' Farbe (Zeiger neben dem Bild) verschwindet sie.</para></summary>
    Public Class PickColorReadout
        Inherits UserControl

        Private Const PointerOffset As Double = 16.0

        Public Sub New()
            AvaloniaXamlLoader.Load(Me)
            ' Gleich breite Ziffern, damit die Anzeige beim Ziehen nicht zappelt.
            Dim text = Me.FindControl(Of TextBlock)("ReadoutText")
            If text IsNot Nothing Then
                text.FontFeatures = New FontFeatureCollection() From {
                    New FontFeature With {.Tag = "tnum", .Value = 1}
                }
            End If
        End Sub

        ''' <summary>Die zuletzt gezeigte Farbe, Nothing wenn ausgeblendet. Fuer den Pruefstand.</summary>
        Public ReadOnly Property ShownColor As Color?
            Get
                Return _shownColor
            End Get
        End Property
        Private _shownColor As Color? = Nothing

        ''' <summary>Der gezeigte Text. Fuer den Pruefstand.</summary>
        Public ReadOnly Property ShownText As String
            Get
                Return If(Me.FindControl(Of TextBlock)("ReadoutText")?.Text, "")
            End Get
        End Property

        ''' <summary>Zeigt die Farbe neben dem Zeiger oder blendet die Anzeige aus.</summary>
        ''' <param name="host">Der Canvas, in dem die Anzeige liegt; seine Groesse bestimmt das
        ''' Umklappen an den Raendern.</param>
        ''' <param name="position">Die Zeigerposition im Canvas.</param>
        ''' <param name="color">Die Farbe, die die Pipette uebernaehme, samt Alpha nur, wenn sie
        ''' Alpha uebernimmt; Nothing blendet aus.</param>
        Public Sub ShowAt(host As Canvas, position As Point, color As Color?)
            If Not color.HasValue Then
                HideReadout()
                Return
            End If

            Dim c = color.Value
            _shownColor = c
            Dim swatch = Me.FindControl(Of Border)("Swatch")
            If swatch IsNot Nothing Then swatch.Background = New SolidColorBrush(c)
            ' Das Schachbrett nur bei Transparenz: ein deckendes Feld verdeckt es ohnehin, aber
            ' an den runden Ecken schiene es sonst durch.
            Dim swatchBackground = Me.FindControl(Of Border)("SwatchBackground")
            If swatchBackground IsNot Nothing Then
                swatchBackground.Background = If(c.A < 255,
                                                 TransparencyBrushService.GetBrush("Checkerboard", ""),
                                                 Nothing)
            End If
            Dim text = Me.FindControl(Of TextBlock)("ReadoutText")
            ' Load() liest nicht die Datei, sondern gibt den zwischengespeicherten Stand zurueck;
            ' eine Aenderung in den Einstellungen gilt damit beim naechsten Schritt.
            If text IsNot Nothing Then
                text.Text = PickerReadoutFormatter.FormatPickerReadout(
                    AppSettingsService.Load().PickerReadout, c.R, c.G, c.B, c.A)
            End If
            IsVisible = True

            ' Gemessen wird erst nach dem Setzen des Texts: die Breite haengt an ihm.
            Measure(Size.Infinity)
            Dim readoutSize = DesiredSize
            Dim left = position.X + PointerOffset
            Dim top = position.Y + PointerOffset
            If host IsNot Nothing Then
                If left + readoutSize.Width > host.Bounds.Width Then left = position.X - PointerOffset - readoutSize.Width
                If top + readoutSize.Height > host.Bounds.Height Then top = position.Y - PointerOffset - readoutSize.Height
            End If
            Canvas.SetLeft(Me, Math.Max(0, left))
            Canvas.SetTop(Me, Math.Max(0, top))
        End Sub

        Public Sub HideReadout()
            _shownColor = Nothing
            IsVisible = False
        End Sub

    End Class

End Namespace
