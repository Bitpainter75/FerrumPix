Imports System
Imports System.Collections.Generic
Imports System.Linq

Namespace Services

    ''' <summary>Die Stichwoerter einer Datei aus ALLEN Quellen in den Katalog - EIN Weg fuer den
    ''' Ordnerlauf der Galerie und den Katalogindex.
    '''
    ''' Vorher hatte jeder der beiden seine eigene Auswahl: die Galerie las die XMP-Beistelldatei, der
    ''' Index nur die .fpxmp. Ein Archiv mit Beistelldateien war deshalb nach einem vollstaendigen
    ''' Indexlauf trotzdem nicht durchsuchbar, bis jemand den Ordner einmal geoeffnet hatte - bei
    ''' einem Netzarchiv also nie. Dass die Beistelldatei im Frische-Stempel steht
    ''' (<see cref="LibraryService.SidecarStamp"/>), machte es nur schlimmer: eine geaenderte .xmp
    ''' loeste einen neuen Durchlauf aus, und der sah ihre Stichwoerter dann nicht an.
    '''
    ''' DREI QUELLEN, EINE MENGE. Vereinigt wird ueber alle: die Beistelldatei (dc:subject,
    ''' lr:hierarchicalSubject) und die Bilddatei selbst (IPTC 2:25 sowie dasselbe XMP-Paar
    ''' eingebettet). Die .fpxmp bleibt aussen vor, weil sie ihren eigenen Weg hat
    ''' (<see cref="LibraryService.ImportFpxmpCatalogData"/>) und dort ERSETZT statt vereinigt.
    '''
    ''' Ihr Stichwortblock sperrt die anderen beiden aber NICHT mehr aus. Er galt einmal als bewusste
    ''' Liste und schlug alles andere; da jede lokale Aenderung ohnehin in die .fpxmp gespiegelt wird,
    ''' entstand daraus eine Falle: der erste Import schrieb die Stichwoerter hinein, und von da an
    ''' kam aus der Beistelldatei nichts Neues mehr durch. Wer in Lightroom oder Bridge nachtraeglich
    ''' ein Stichwort ergaenzte, sah es in FerrumPix nie wieder.</summary>
    Public NotInheritable Class KeywordImportService

        Private Sub New()
        End Sub

        ''' <summary>Beistelldatei selbst lesen und zusammen mit den eingebetteten Stichwoertern
        ''' uebernehmen. Der Einstieg fuer den Katalogindex, der die Datei sonst nicht anfasst.</summary>
        Public Shared Function Import(filePath As String, embeddedKeywords As IEnumerable(Of String)) As List(Of String)
            Return Import(filePath, embeddedKeywords, ReadSidecarKeywords(filePath))
        End Function

        ''' <summary>Dieselbe Uebernahme fuer einen Aufrufer, der die Beistelldatei SCHON gelesen hat.
        ''' Ein zweites Lesen je Bild waere ein zweiter Plattenzugriff fuer nichts.</summary>
        Public Shared Function Import(filePath As String,
                                      embeddedKeywords As IEnumerable(Of String),
                                      sidecarKeywords As IEnumerable(Of String)) As List(Of String)
            Dim all As New List(Of String)()
            ' Die Beistelldatei zuerst: sie ist die speziellere Angabe, und bei gleicher Schreibweise
            ' behaelt die Vereinigung den ERSTEN Eintrag.
            If sidecarKeywords IsNot Nothing Then all.AddRange(sidecarKeywords)
            If embeddedKeywords IsNot Nothing Then all.AddRange(embeddedKeywords)
            ' Eigene, optional nach XMP geschriebene KI-Tags bleiben getrennt von Handarbeit
            ' (siehe LibraryService.WithoutOwnAiTags).
            all = LibraryService.Instance.WithoutOwnAiTags(filePath, all)
            If all Is Nothing OrElse all.Count = 0 Then Return Nothing
            Return LibraryService.Instance.ImportFileKeywords(filePath, all)
        End Function

        ''' <summary>Die Stichwoerter der XMP-Beistelldatei, falls eine danebenliegt.</summary>
        Public Shared Function ReadSidecarKeywords(filePath As String) As List(Of String)
            Try
                Dim sidecarPath = XmpSidecarService.FindSidecar(filePath)
                If String.IsNullOrEmpty(sidecarPath) Then Return Nothing
                Return XmpSidecarService.ReadSidecar(sidecarPath)?.Keywords
            Catch ex As Exception
                DiagnosticLogService.LogException("Stichworte.Beistelldatei", ex)
                Return Nothing
            End Try
        End Function

    End Class

End Namespace
