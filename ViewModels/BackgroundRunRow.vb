Imports System.Windows.Input
Imports ReactiveUI

Namespace ViewModels

    ''' <summary>
    ''' EIN laufender Vorgang in der Anzeige an der Stelle des Suchfelds: sein Text, sein Balken und
    ''' der Knopf zum Anhalten.
    '''
    ''' Vier Vorgaenge teilen sich diese Anzeige - der Katalogindex, die Gesichtssuche ueber die
    ''' ueberwachten Ordner, die Gesichtssuche ueber die angezeigten Bilder und eine laufende
    ''' Suchliste. Sie kommen aus ganz verschiedenen Ecken: die ersten beiden bringen ein eigenes
    ''' <see cref="BackgroundRunViewModel"/> mit, die anderen beiden fuehrt die Galerie selbst.
    ''' Deshalb steht hier nur, was die ANZEIGE braucht; wer den Vorgang fuehrt, spielt fuer sie
    ''' keine Rolle.
    '''
    ''' Eine eigene Zeile je Vorgang und nicht ein Satz Eigenschaften am ViewModel: laufen zwei
    ''' gleichzeitig, stehen sie nebeneinander und teilen sich den Platz, statt dass einer den
    ''' anderen verdeckt.
    ''' </summary>
    Public NotInheritable Class BackgroundRunRow
        Inherits ViewModelBase

        Private _text As String = ""
        Private _percent As Double
        Private _hasProgress As Boolean

        Public Sub New(stopCommand As ICommand)
            Me.StopCommand = stopCommand
        End Sub

        ''' <summary>Haelt den Vorgang an. Was bis dahin getan wurde, bleibt.</summary>
        Public ReadOnly Property StopCommand As ICommand

        ''' <summary>Was gerade geschieht, in einem Satz.</summary>
        Public Property Text As String
            Get
                Return _text
            End Get
            Set(value As String)
                Me.RaiseAndSetIfChanged(_text, If(value, ""))
            End Set
        End Property

        ''' <summary>Fortschritt von 0 bis 100.</summary>
        Public Property Percent As Double
            Get
                Return _percent
            End Get
            Set(value As Double)
                Me.RaiseAndSetIfChanged(_percent, value)
            End Set
        End Property

        ''' <summary>Steht die Gesamtzahl schon? Solange nicht, laeuft der Balken unbestimmt, statt
        ''' auf null stehenzubleiben. Eine Suchliste weiss ihre Gesamtzahl nie - sie zaehlt erst beim
        ''' Durchgehen, wie viele Bilder es ueberhaupt zu pruefen gibt.</summary>
        Public Property HasProgress As Boolean
            Get
                Return _hasProgress
            End Get
            Set(value As Boolean)
                Me.RaiseAndSetIfChanged(_hasProgress, value)
            End Set
        End Property

    End Class

End Namespace
