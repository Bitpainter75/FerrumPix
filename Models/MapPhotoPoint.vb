Namespace Models

    ''' <summary>Ein Bild mit Aufnahmeort, wie es die Karte zeichnet. Die Koordinaten kommen aus
    ''' dem Katalog und nicht aus dem ImageItem, das sie nicht trägt.</summary>
    Public NotInheritable Class MapPhotoPoint

        Public Sub New(item As ImageItem, latitude As Double, longitude As Double)
            Me.Item = item
            Me.Latitude = latitude
            Me.Longitude = longitude
        End Sub

        Public ReadOnly Property Item As ImageItem
        Public ReadOnly Property Latitude As Double
        Public ReadOnly Property Longitude As Double
    End Class

End Namespace
