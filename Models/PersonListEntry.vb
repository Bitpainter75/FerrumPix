Imports System.ComponentModel
Imports System.Runtime.CompilerServices
Imports Avalonia.Media.Imaging
Imports FerrumPix.Services

Namespace Models

    ''' <summary>
    ''' Eine Person in der Verwaltung: ihr Gesicht, ihr Name, ihre Anzahl.
    '''
    ''' EIGENE KLASSE neben PersonFilterOption und PersonFaceEntry, und das ist kein Wildwuchs -
    ''' die drei zeigen VERSCHIEDENES. Im Filter zaehlt der Haken, am Bild zaehlt, welches Gesicht
    ''' gemeint ist, und hier zaehlt das Aushaengeschild: ein grosses, scharfes Gesicht, an dem man
    ''' die Person erkennt, ohne den Namen zu lesen.
    ''' </summary>
    Public Class PersonListEntry
        Implements INotifyPropertyChanged

        Private _name As String
        Private _cover As Bitmap
        Private _imageCount As Integer

        Public Sub New(id As String, name As String, imageCount As Integer,
                       Optional isUnknownBin As Boolean = False)
            Me.Id = If(id, "")
            _name = If(name, "")
            _imageCount = imageCount
            Me.IsUnknownBin = isUnknownBin
        End Sub

        Public ReadOnly Property Id As String

        ''' <summary>Wie viele Bilder die Gruppe traegt. Aenderbar, weil ein herausgeloestes Gesicht
        ''' die Zahl sofort falsch machen wuerde - und eine Zahl, die nicht stimmt, ist schlimmer als
        ''' keine.</summary>
        Public Property ImageCount As Integer
            Get
                Return _imageCount
            End Get
            Set(value As Integer)
                If _imageCount = value Then Return
                _imageCount = value
                Notify()
                Notify(NameOf(CountText))
            End Set
        End Property

        ''' <summary>Der Ablagekorb fuer herausgeloeste Gesichter. Er steht in der Wand wie eine
        ''' Gruppe, ist aber keine Person - deshalb traegt er einen eigenen Namen.</summary>
        Public ReadOnly Property IsUnknownBin As Boolean

        Public Property Name As String
            Get
                Return _name
            End Get
            Set(value As String)
                If String.Equals(_name, value, StringComparison.Ordinal) Then Return
                _name = If(value, "")
                Notify()
                Notify(NameOf(DisplayName))
                Notify(NameOf(IsNamed))
            End Set
        End Property

        Public ReadOnly Property IsNamed As Boolean
            Get
                Return Not String.IsNullOrWhiteSpace(_name)
            End Get
        End Property

        ''' <summary>Was auf der Kachel steht. Eine namenlose Gruppe bekommt einen Platzhalter statt
        ''' einer leeren Zeile - sie ist ja gerade die, die Arbeit braucht.</summary>
        Public ReadOnly Property DisplayName As String
            Get
                If IsUnknownBin Then Return LocalizationService.T("Unbekannt")
                Return If(IsNamed, _name, LocalizationService.T("Ohne Namen"))
            End Get
        End Property

        Public ReadOnly Property CountText As String
            Get
                Return $"{ImageCount} " & LocalizationService.T("Bilder")
            End Get
        End Property

        ''' <summary>Wo das Aushaengeschild herkommt: Datei und Lage des groessten Gesichts.</summary>
        Public Property CoverPath As String = ""
        Public Property BoxX As Double
        Public Property BoxY As Double
        Public Property BoxWidth As Double
        Public Property BoxHeight As Double

        ''' <summary>Das fertige Bild. Kommt im Hintergrund nach - bei hundert Gruppen waere ein
        ''' Decode je Kachel im Vordergrund eine Vollbremsung.</summary>
        Public Property Cover As Bitmap
            Get
                Return _cover
            End Get
            Set(value As Bitmap)
                _cover = value
                Notify()
            End Set
        End Property

        Public Event PropertyChanged As PropertyChangedEventHandler Implements INotifyPropertyChanged.PropertyChanged

        Private Sub Notify(<CallerMemberName> Optional name As String = Nothing)
            RaiseEvent PropertyChanged(Me, New PropertyChangedEventArgs(name))
        End Sub

    End Class

End Namespace
