Namespace Models

    ''' <summary>Woher ein offenes Bild auf einem Nextcloud-Server stammt.
    '''
    ''' EIN BUENDEL statt einer wachsenden Reihe von Parametern: der Weg in den Editor braucht
    ''' inzwischen drei Angaben, und jede fuer sich durch Galerie, Betrachter, Anwendungsrumpf und
    ''' Editor zu reichen hiess, an vier Stellen dieselbe Signatur zu verlaengern. Wer eine vierte
    ''' Angabe braucht, aendert nur diese Klasse.
    '''
    ''' Die drei Angaben tun Verschiedenes und sind deshalb alle noetig:
    ''' - <see cref="PathInTree"/> ist der Ort auf dem Server. Daran haengt, wohin die Begleitdatei
    '''   kommt und was ein Ersetzen ueberschreibt.
    ''' - <see cref="ETag"/> ist der STAND, den dieses Bild beim Oeffnen hatte. Er geht als Bedingung
    '''   an das Ersetzen, damit eine fremde Aenderung nicht lautlos ueberschrieben wird.
    ''' - <see cref="PseudoPath"/> ist die IDENTITAET im lokalen Katalog. Bewertung und Farbetikett
    '''   kennt der Server nicht, sie bleiben lokal - aber unter diesem stabilen Schluessel und
    '''   nicht unter dem Pfad der Temp-Kopie, den das naechste Aufraeumen wegnimmt.</summary>
    Public Class NextcloudOrigin

        Public Property PathInTree As String = ""
        Public Property ETag As String = ""
        Public Property PseudoPath As String = ""

        ''' <summary>Ist die Herkunft ueberhaupt bekannt? Ohne den Pfad gibt es keinen Rueckweg, und
        ''' der Editor bleibt bei "Speichern unter".</summary>
        Public ReadOnly Property IsKnown As Boolean
            Get
                Return Not String.IsNullOrEmpty(PathInTree)
            End Get
        End Property

        ''' <summary>Die Herkunft eines Elements, oder Nothing, wenn es nicht von diesem Server
        ''' stammt. EINE Stelle, an der die drei Angaben zusammengesucht werden.</summary>
        Public Shared Function FromItem(item As ImageItem) As NextcloudOrigin
            If item Is Nothing OrElse Not item.IsNextcloudAsset Then Return Nothing
            Return New NextcloudOrigin With {
                .PathInTree = If(item.NextcloudPath, ""),
                .ETag = If(item.NextcloudETag, ""),
                .PseudoPath = If(item.FilePath, "")
            }
        End Function

    End Class

End Namespace
