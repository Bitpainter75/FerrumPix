Imports System

Namespace Services

    ''' <summary>Ein gemerkter Aufnahmeort, zwischen "kopieren" und "einfuegen".
    '''
    ''' Der haeufigste Weg, wie Koordinaten an Bilder kommen, die keine haben: EIN Bild der Reihe
    ''' traegt sie (das vom Telefon), die anderen nicht (die aus der Kamera), und alle sind am
    ''' selben Tag am selben Ort entstanden. Dafuer braucht es keinen Dialog, nur zwei
    ''' Menueeintraege.
    '''
    ''' BEWUSST NICHT DIE ZWISCHENABLAGE DES SYSTEMS. Dort liegen Dateien und Text, und ein
    ''' Aufnahmeort waere weder das eine noch das andere; er wuerde ausserdem beim naechsten
    ''' Kopieren einer Datei verlorengehen. Der Merker gilt nur in dieser Anwendung und nur bis zum
    ''' Beenden - er beschreibt eine Geste, keinen Zustand, der eine Ablage verdient.</summary>
    Public NotInheritable Class GeotagClipboard

        Private Sub New()
        End Sub

        Private Shared ReadOnly _lock As New Object()
        Private Shared _latitude As Double?
        Private Shared _longitude As Double?
        Private Shared _label As String = ""

        Public Shared ReadOnly Property HasCoordinate As Boolean
            Get
                SyncLock _lock
                    Return _latitude.HasValue AndAlso _longitude.HasValue
                End SyncLock
            End Get
        End Property

        ''' <summary>Woher der Ort stammt - Ortsname, sonst die Koordinate. Steht in der
        ''' Beschriftung des Einfuegen-Eintrags, damit sichtbar ist, was da eingefuegt wird.</summary>
        Public Shared ReadOnly Property Label As String
            Get
                SyncLock _lock
                    Return _label
                End SyncLock
            End Get
        End Property

        Public Shared Sub Remember(latitude As Double, longitude As Double, label As String)
            If Not GeotagService.IsValidCoordinate(latitude, longitude) Then Return
            SyncLock _lock
                _latitude = latitude
                _longitude = longitude
                _label = If(label, "").Trim()
                If _label.Length = 0 Then _label = GeotagService.FormatCoordinates(latitude, longitude)
            End SyncLock
        End Sub

        Public Shared Function TryGet(ByRef latitude As Double, ByRef longitude As Double) As Boolean
            SyncLock _lock
                If Not _latitude.HasValue OrElse Not _longitude.HasValue Then Return False
                latitude = _latitude.Value
                longitude = _longitude.Value
                Return True
            End SyncLock
        End Function

        Public Shared Sub Clear()
            SyncLock _lock
                _latitude = Nothing
                _longitude = Nothing
                _label = ""
            End SyncLock
        End Sub

    End Class

End Namespace
