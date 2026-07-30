Imports System
Imports System.Collections.Generic
Imports System.Windows.Input

Namespace Models

    ''' <summary>
    ''' Ein Eintrag des Fussmenues als DATEN statt als Markup.
    '''
    ''' Vorher stand jede Aktion in jeder Ansicht einzeln im XAML, samt Beschriftung und fuenf
    ''' Zeilen Symbol. Das lief sofort auseinander: dieselbe Aktion hiess einmal "Im Dateimanager
    ''' anzeigen" und einmal "Im Dateimanager zeigen", und die Galerie schrieb "Bildgroesse
    ''' aendern..." mit drei Punkten. Mit einer Liste kann das nicht mehr passieren - Beschriftung
    ''' und Symbol stehen genau einmal, im Katalog.
    '''
    ''' Welche Aktionen ein Bereich zeigt, entscheidet er selbst, indem er seine Liste
    ''' zusammenstellt. Eine eigene Sichtbarkeitsmarke braucht es dafuer nicht: was nicht in der
    ''' Liste steht, erscheint auch nicht.
    ''' </summary>
    Public NotInheritable Class AppAction

        Public Sub New(label As String, iconName As String, command As ICommand,
                       Optional parameter As Object = Nothing,
                       Optional children As IReadOnlyList(Of Object) = Nothing)
            Me.Label = label
            Me.IconSource = If(String.IsNullOrEmpty(iconName), "",
                               "avares://FerrumPix/Assets/Icons/outline/" & iconName & ".svg")
            Me.Command = command
            Me.Parameter = parameter
            Me.Children = children
        End Sub

        ''' Fuer Kommandos, die einen Wert brauchen: die Bewertung als Zahl, das Etikett als Farbe.
        Public ReadOnly Property Parameter As Object

        ''' Untereintraege. Nothing heisst: kein Untermenue. Bewertung und Etikett haben je sechs
        ''' bzw. zehn Werte - flach nebeneinander sprengten sie das Menue.
        Public ReadOnly Property Children As IReadOnlyList(Of Object)

        ''' Bereits UEBERSETZT. Die Liste wird bei jedem Oeffnen neu gebaut, damit ein
        ''' Sprachwechsel ohne Umweg ankommt.
        Public ReadOnly Property Label As String
        Public ReadOnly Property IconSource As String
        Public ReadOnly Property Command As ICommand

    End Class

End Namespace
