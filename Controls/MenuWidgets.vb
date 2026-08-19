Imports System.Collections.Generic
Imports System.Windows.Input
Imports Avalonia
Imports Avalonia.Controls
Imports Avalonia.Controls.Shapes
Imports Avalonia.Layout
Imports Avalonia.Media
Imports FerrumPix.Models
Imports FerrumPix.Services
Imports FerrumPix.ViewModels

Namespace Controls

    ''' <summary>
    ''' Zeilen fuer das Kontextmenue, die keine Eintraege sind, sondern kleine Bedienelemente:
    ''' die Bewertungssterne mit Herz und die bunten Kreise der Farbetiketten.
    '''
    ''' Warum nicht als Untermenue: fuenf Sterne und zehn Farben nebeneinander sind mit einem Blick
    ''' zu erfassen und mit einem Klick gesetzt. Als Untermenue braucht dasselbe zwei Klicks, und
    ''' aus den Farben werden Textzeilen, die man lesen muss statt sie zu sehen.
    '''
    ''' Ein Menue nimmt Objekte aus seiner Liste, die bereits Bedienelemente SIND, unveraendert als
    ''' eigene Zeile - genauso wie einen Trenner. Deshalb entstehen die Zeilen hier fertig.
    ''' </summary>
    Public NotInheritable Class MenuWidgets

        ' Dieselben Farben wie die Akzentauswahl in den Einstellungen und wie das Filtermenue.
        Private Shared ReadOnly LabelColors As (Name As String, Hex As String)() = {
            ("Orange", "#F08A1A"), ("Rot", "#E74C3C"), ("Pink", "#F03B88"),
            ("Lila", "#8B5CF6"), ("Blau", "#3B82F6"), ("Cyan", "#0891B2"),
            ("Türkis", "#0F766E"), ("Grün", "#22C55E"), ("Gelb", "#FACC15")}

        ''' <summary>Dieselben neun Farben, nur die Werte - in derselben Reihenfolge, in der sie im
        ''' Menue stehen. Die Tastenkuerzel ALT+1 bis ALT+9 belegen genau diese Reihenfolge und
        ''' lesen sie HIER, damit Menue und Tastatur nicht auseinanderlaufen koennen.</summary>
        Public Shared ReadOnly Property LabelColorValues As IReadOnlyList(Of String) =
            Array.ConvertAll(LabelColors, Function(color) color.Hex)

        Private Sub New()
        End Sub

        ''' <summary>Sterne, Bewertung loeschen und Favorit in einer Zeile.
        '''
        ''' Die Zeile fuehrt sich SELBST nach. Ein Klick landet auf der Schaltflaeche und nicht auf
        ''' dem Menueeintrag darum - das Menue bleibt also offen. Waere die Anzeige nur beim Bauen
        ''' gesetzt, saehe man seine eigene Bewertung erst beim naechsten Oeffnen.
        '''
        ''' Der Zaehler daneben spiegelt dieselbe Regel wie die Kommandos: derselbe Stern noch
        ''' einmal setzt auf keine Bewertung zurueck.</summary>
        Public Shared Function RatingRow(rating As Integer, isFavorite As Boolean,
                                         setRating As ICommand, toggleFavorite As ICommand) As Control
            If setRating Is Nothing AndAlso toggleFavorite Is Nothing Then Return Nothing

            Dim row = NewRow()
            Dim current = rating
            Dim stars As New List(Of Button)()

            Dim showRating As Action =
                Sub()
                    For index = 0 To stars.Count - 1
                        Dim shouldBeActive = current >= index + 1
                        If shouldBeActive AndAlso Not stars(index).Classes.Contains("active") Then
                            stars(index).Classes.Add("active")
                        ElseIf Not shouldBeActive Then
                            stars(index).Classes.Remove("active")
                        End If
                    Next
                End Sub

            If setRating IsNot Nothing Then
                For star = 1 To 5
                    Dim wanted = star
                    Dim button As New Button With {
                        .Background = Brushes.Transparent,
                        .BorderThickness = New Thickness(0),
                        .Padding = New Thickness(0),
                        .Width = ButtonSize,
                        .Height = ButtonSize,
                        .Command = New DelegateCommand(
                            Sub()
                                setRating.Execute(wanted.ToString())
                                current = If(current = wanted, 0, wanted)
                                showRating()
                            End Sub)
                    }
                    button.Classes.Add("star-btn")
                    button.Classes.Add("compact")

                    Dim icon As New SvgIcon With {
                        .Source = "avares://FerrumPix/Assets/Icons/outline/star.svg",
                        .Width = 16,
                        .Height = 16
                    }
                    icon.Classes.Add("rating-star")
                    button.Content = icon
                    stars.Add(button)
                    row.Children.Add(button)
                Next
                showRating()

                row.Children.Add(ClearButton(New DelegateCommand(
                    Sub()
                        setRating.Execute("0")
                        current = 0
                        showRating()
                    End Sub), Nothing, "Keine Bewertung"))
            End If

            If toggleFavorite IsNot Nothing Then
                Dim isSet = isFavorite
                Dim icon As New SvgIcon With {
                    .Source = "avares://FerrumPix/Assets/Icons/outline/heart.svg",
                    .Width = 18,
                    .Height = 18
                }
                icon.Classes.Add("fav-heart")
                If isSet Then icon.Classes.Add("active")

                Dim heart As New Button With {
                    .Background = Brushes.Transparent,
                    .BorderThickness = New Thickness(0),
                    .Padding = New Thickness(0),
                    .Width = ButtonSize,
                    .Height = ButtonSize,
                    .Margin = New Thickness(6, 0, 0, 0),
                    .Content = icon,
                    .Command = New DelegateCommand(
                        Sub()
                            toggleFavorite.Execute(Nothing)
                            isSet = Not isSet
                            If isSet Then
                                icon.Classes.Add("active")
                            Else
                                icon.Classes.Remove("active")
                            End If
                        End Sub)
                }
                heart.Classes.Add("favorite-btn")
                ToolTip.SetTip(heart, LocalizationService.T("Favorit"))
                row.Children.Add(heart)
            End If

            Return AsMenuRow(row)
        End Function

        ''' <summary>Die Farbetiketten als Kreise, dahinter das Kreuz zum Entfernen. Das gesetzte
        ''' Etikett traegt einen Haken, und der wandert beim Klick sofort mit - wie bei den Sternen
        ''' bleibt das Menue dabei offen.</summary>
        Public Shared Function ColorLabelRow(currentLabel As String, setLabel As ICommand) As Control
            If setLabel Is Nothing Then Return Nothing

            Dim row = NewRow()
            Dim current = If(currentLabel, "")
            Dim marks As New Dictionary(Of String, TextBlock)(StringComparer.OrdinalIgnoreCase)

            Dim showLabel As Action =
                Sub()
                    For Each pair In marks
                        pair.Value.IsVisible = String.Equals(pair.Key, current, StringComparison.OrdinalIgnoreCase)
                    Next
                End Sub

            For Each entry In LabelColors
                Dim hex = entry.Hex
                ' Der Haken liegt UEBER dem Kreis, deshalb ein Raster statt einer Ellipse allein.
                Dim mark As New TextBlock With {
                    .Text = "✓",
                    .FontSize = 11,
                    .FontWeight = FontWeight.Bold,
                    .HorizontalAlignment = HorizontalAlignment.Center,
                    .VerticalAlignment = VerticalAlignment.Center,
                    .Foreground = If(String.Equals(hex, "#FACC15", StringComparison.OrdinalIgnoreCase),
                                     Brushes.Black, Brushes.White),
                    .IsVisible = False
                }
                marks(hex) = mark

                Dim stack As New Panel()
                stack.Children.Add(New Ellipse With {
                    .Width = 16,
                    .Height = 16,
                    .Fill = New SolidColorBrush(Color.Parse(hex))
                })
                stack.Children.Add(mark)

                Dim button As New Button With {
                    .Padding = New Thickness(0),
                    .Width = ButtonSize,
                    .Height = ButtonSize,
                    .CornerRadius = New CornerRadius(ButtonSize / 2),
                    .Content = stack,
                    .Command = New DelegateCommand(
                        Sub()
                            setLabel.Execute(hex)
                            ' Dieselbe Farbe noch einmal nimmt das Etikett weg - genau wie die
                            ' Kommandos dahinter es handhaben.
                            current = If(String.Equals(current, hex, StringComparison.OrdinalIgnoreCase), "", hex)
                            showLabel()
                        End Sub)
                }
                button.Classes.Add("ghost")
                ToolTip.SetTip(button, LocalizationService.T(entry.Name))
                row.Children.Add(button)
            Next
            showLabel()

            row.Children.Add(ClearButton(New DelegateCommand(
                Sub()
                    setLabel.Execute("")
                    current = ""
                    showLabel()
                End Sub), Nothing, "Kein Etikett"))
            Return AsMenuRow(row)
        End Function

        ''' <summary>Die Zeile als Menueeintrag verpacken.
        '''
        ''' Ein Menue verpackt jedes Element, das selbst KEIN Menueeintrag ist, in einen. Damit
        ''' greift auch die Vorlage des Menues: sie setzt Beschriftung, Kommando und Untereintraege
        ''' aus Eigenschaften, die es hier nicht gibt. Erst blieb die Zeile dadurch leer, danach
        ''' liess sich nichts anklicken.
        '''
        ''' Deshalb die Klasse "widget": die Vorlage im Menue nimmt sie ausdruecklich aus. Was hier
        ''' steht, steht damit allein hier - der Menueeintrag ist nur noch der Rahmen darum.</summary>
        Private Shared Function AsMenuRow(content As Control) As MenuItem
            Dim row As New MenuItem With {
                .Header = content,
                .Padding = New Thickness(0),
                .MinWidth = 0
            }
            row.Classes.Add("widget")
            Return row
        End Function

        ''' <summary>Die Zeile mit den zehn Farbkreisen ist das BREITESTE im ganzen Menue und
        ''' bestimmt damit dessen Breite - eine Mindestbreite an den Eintraegen aendert daran
        ''' nichts. Wer das Menue schmaler haben will, aendert deshalb hier: Knopfgroesse und
        ''' Abstand. Zehn mal <see cref="ButtonSize"/> plus neun mal Abstand plus Rand.</summary>
        Private Const ButtonSize As Integer = 22
        Private Const RowSpacing As Integer = 4

        ''' <summary>Seitlich KEIN eigener Rand. Der Kopfbereich eines Menueeintrags polstert schon
        ''' 14 Punkt, und genau dort beginnen auch die Symbole der uebrigen Eintraege - ein
        ''' zusaetzlicher Rand rueckte die Zeilen sichtbar gegen sie ein.</summary>
        Private Shared Function NewRow() As StackPanel
            Return New StackPanel With {
                .Orientation = Orientation.Horizontal,
                .Spacing = RowSpacing,
                .Margin = New Thickness(0, 4, 0, 4)
            }
        End Function

        ''' <summary>Das Kreuz zum Zuruecksetzen - dieselbe Form wie die Kreise daneben, damit die
        ''' Zeile eine Reihe bleibt.</summary>
        Private Shared Function ClearButton(command As ICommand, parameter As String, tip As String) As Button
            Dim button As New Button With {
                .Padding = New Thickness(0),
                .Width = ButtonSize,
                .Height = ButtonSize,
                .CornerRadius = New CornerRadius(ButtonSize / 2),
                .Command = command,
                .CommandParameter = parameter,
                .Content = New TextBlock With {
                    .Text = "×",
                    .FontSize = 14,
                    .HorizontalAlignment = HorizontalAlignment.Center,
                    .VerticalAlignment = VerticalAlignment.Center
                }
            }
            button.Classes.Add("ghost")
            ToolTip.SetTip(button, LocalizationService.T(tip))
            Return button
        End Function

    End Class

End Namespace
