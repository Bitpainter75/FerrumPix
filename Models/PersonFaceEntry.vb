Imports System.ComponentModel
Imports System.Runtime.CompilerServices
Imports Avalonia.Media.Imaging
Imports FerrumPix.Services

Namespace Models

    ''' <summary>
    ''' Ein Gesicht auf dem gerade markierten Bild: der Ausschnitt, der Name und die Person dahinter.
    '''
    ''' EIGENE KLASSE neben PersonFilterOption, weil hier etwas anderes gezeigt wird. Im Filter
    ''' zaehlt, wie viele Bilder eine Person hat; hier zaehlt, WELCHES Gesicht gemeint ist. Ohne den
    ''' Ausschnitt waeren fuenf Namensfelder untereinander nicht zuzuordnen - man saehe fuenfmal
    ''' "Name eingeben" und wuesste nicht, wer wer ist.
    ''' </summary>
    Public Class PersonFaceEntry
        Implements INotifyPropertyChanged

        Private _name As String
        Private _thumbnail As Bitmap

        Public Sub New(faceId As String, personId As String, name As String)
            Me.FaceId = If(faceId, "")
            Me.Id = If(personId, "")
            _name = If(name, "")
        End Sub

        ''' <summary>Dieses eine Gesicht auf diesem einen Bild.
        '''
        ''' Neben der Person-Id, weil beide verschiedene Reichweiten haben: der NAME gilt fuer die
        ''' ganze Gruppe und damit fuer jedes Bild, das Herausloesen einer Fehlzuordnung dagegen nur
        ''' fuer dieses Gesicht hier.</summary>
        Public ReadOnly Property FaceId As String

        Public ReadOnly Property Id As String

        ''' <summary>Der Name der Gruppe. Beschreibbar, weil das Feld im Panel direkt darauf schreibt.</summary>
        Public Property Name As String
            Get
                Return _name
            End Get
            Set(value As String)
                If String.Equals(_name, value, StringComparison.Ordinal) Then Return
                _name = If(value, "")
                Notify()
                Notify(NameOf(IsNamed))
            End Set
        End Property

        ''' <summary>Die Lage des Gesichts im Bild, in Bildpunkten. Sie wird gemerkt, damit der
        ''' Ausschnitt im Hintergrund nachgeholt werden kann, ohne die Datenbank ein zweites Mal zu
        ''' fragen.</summary>
        Public ReadOnly Property BoxX As Double
        Public ReadOnly Property BoxY As Double
        Public ReadOnly Property BoxWidth As Double
        Public ReadOnly Property BoxHeight As Double

        ''' <summary>Die Datei, auf der dieses Gesicht steht. Fuer die Vorschau hinter dem
        ''' Auge-Zeichen: eine Kachel von 84 Punkten zeigt ein Gesicht, aber nicht, ob der Ausschnitt
        ''' ueberhaupt zu dem Bild passt, das man im Kopf hat. Leer, wo die Zeile aus dem Infopanel
        ''' kommt - dort steht das Bild ohnehin daneben.</summary>
        Public Property FilePath As String = ""

        Public Sub SetBox(x As Double, y As Double, width As Double, height As Double)
            _BoxX = x
            _BoxY = y
            _BoxWidth = width
            _BoxHeight = height
        End Sub

        ''' <summary>Der Gesichtsausschnitt. Nothing, wenn die Datei nicht lesbar war - dann bleibt
        ''' das Feld allein stehen, was immer noch besser ist als gar keine Zeile.</summary>
        Public Property Thumbnail As Bitmap
            Get
                Return _thumbnail
            End Get
            Set(value As Bitmap)
                _thumbnail = value
                Notify()
            End Set
        End Property

        Public ReadOnly Property IsNamed As Boolean
            Get
                Return Not String.IsNullOrWhiteSpace(_name)
            End Get
        End Property

        Public Event PropertyChanged As PropertyChangedEventHandler Implements INotifyPropertyChanged.PropertyChanged

        Private Sub Notify(<CallerMemberName> Optional name As String = Nothing)
            RaiseEvent PropertyChanged(Me, New PropertyChangedEventArgs(name))
        End Sub

    End Class

End Namespace
