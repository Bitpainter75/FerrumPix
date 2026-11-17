Namespace Models

    ''' <summary>Eine Auswahl im Filter der Galerie nach Aufnahmedaten: eine Kamera, ein Objektiv,
    ''' ein Bereich der Brennweite oder der ISO-Empfindlichkeit, wie er im gezeigten Bestand
    ''' vorkommt.</summary>
    Public Class ShotFilterOption
        Public Sub New(kind As String, key As String, label As String, isSelected As Boolean)
            Me.Kind = kind
            Me.Key = key
            Me.Label = label
            Me.IsSelected = isSelected
        End Sub

        ''' <summary>Zu welcher Gruppe die Auswahl gehoert: Camera, Lens, Focal oder Iso.</summary>
        Public ReadOnly Property Kind As String
        ''' <summary>Der Wert, nach dem gefiltert wird: der Name, bei einem Bereich seine obere Grenze.</summary>
        Public ReadOnly Property Key As String
        Public ReadOnly Property Label As String
        Public ReadOnly Property IsSelected As Boolean
    End Class
End Namespace
