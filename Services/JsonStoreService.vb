Imports System
Imports System.IO

Namespace Services

    ''' <summary>
    ''' Die zwei Handgriffe, die eine JSON-Ablage mit Nutzerdaten braucht, an EINER Stelle.
    '''
    ''' Warum das ueberhaupt eigene Routinen sind: Favoriten und Suchlisten haben frueher mit
    ''' File.WriteAllText direkt in die Zieldatei geschrieben und jeden Fehler mit einem leeren
    ''' Catch geschluckt. Das Lesen fing Parserfehler ebenso ab und lieferte eine LEERE Liste.
    ''' Weil Aendern aus Lesen und Zurueckschreiben besteht, ersetzte der naechste gesetzte
    ''' Favorit den gesamten Bestand durch eine Datei mit genau diesem einen Eintrag - ohne
    ''' jede Meldung. Favoriten und Suchlisten sind Handarbeit und nicht wiederherstellbar.
    '''
    ''' AppSettingsService loest dasselbe seit laengerem selbst und bleibt bewusst eigenstaendig
    ''' (es haelt zusaetzlich einen Zwischenspeicher). Hier steht die Fassung fuer alle anderen,
    ''' damit es keine dritte und vierte Kopie derselben Regel gibt.
    ''' </summary>
    Public NotInheritable Class JsonStoreService

        Private Sub New()
        End Sub

        ''' <summary>
        ''' Schreibt ueber eine Nachbardatei und benennt dann um. NIE direkt in die Zieldatei:
        ''' FileMode.Create kuerzt sie sofort auf 0 Byte, und bricht das Schreiben danach ab
        ''' (volle Platte, Absturz, Stromausfall), ist der alte Inhalt bereits verloren.
        '''
        ''' Liefert False, wenn nichts geschrieben wurde. Der Aufrufer darf das nicht
        ''' stillschweigend uebergehen.
        ''' </summary>
        ''' Der Parameter heisst bewusst NICHT "path": VB unterscheidet keine Gross- und
        ''' Kleinschreibung, "Path.GetDirectoryName" loeste dann auf den Parameter auf.
        Public Shared Function WriteAtomic(zielPfad As String, json As String, area As String) As Boolean
            Try
                Dim ordner = Path.GetDirectoryName(zielPfad)
                If Not String.IsNullOrEmpty(ordner) Then Directory.CreateDirectory(ordner)

                Dim tempPfad = zielPfad & ".tmp"
                File.WriteAllText(tempPfad, json)
                File.Move(tempPfad, zielPfad, overwrite:=True)
                Return True
            Catch ex As Exception
                ' Volle Platte, fehlende Rechte: frueher fiel das lautlos unter den Tisch und der
                ' Nutzer glaubte, seine Aenderung sei gespeichert.
                DiagnosticLogService.LogException(area & ".Save", ex)
                Return False
            End Try
        End Function

        ''' <summary>
        ''' Legt eine unlesbare Datei zur Seite, statt sie beim naechsten Speichern ueberschreiben
        ''' zu lassen. Das ist der wichtigste der beiden Handgriffe: er unterbricht die Kette aus
        ''' leerem Lesen und Zurueckschreiben und macht den Bestand von Hand rettbar.
        ''' </summary>
        Public Shared Sub BackupUnreadable(zielPfad As String, area As String)
            Try
                If Not File.Exists(zielPfad) Then Return
                File.Move(zielPfad, zielPfad & ".corrupt", overwrite:=True)
                DiagnosticLogService.LogAlways(area & ".Load",
                    "Datei war unlesbar und wurde nach " & Path.GetFileName(zielPfad) & ".corrupt gesichert")
            Catch ex As Exception
                DiagnosticLogService.LogException(area & ".Backup", ex)
            End Try
        End Sub

    End Class

End Namespace
