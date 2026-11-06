Imports System.Collections.Generic
Imports System.IO
Imports System.Xml.Linq

Namespace Services

    Public Enum MetadataWriteTarget
        None = 0
        ''' <summary>In die Bilddatei (JPEG, PNG, WebP); eine vorhandene Beistelldatei zog mit.</summary>
        InFile = 1
        ''' <summary>Nur in die XMP-Beistelldatei (alle anderen Formate, vor allem RAW).</summary>
        XmpSidecar = 2
    End Enum

    Public Class MetadataWriteResult
        Public Property Success As Boolean
        Public Property Target As MetadataWriteTarget = MetadataWriteTarget.None
    End Class

    ''' <summary>
    ''' Schreibt eine Metadaten-Aenderung an ein Bild: in die Datei und/oder in ihre
    ''' XMP-Beistelldatei. EIN Ablauf fuer alle, die das tun (Titel und Beschreibung, Bewertung und
    ''' Stichwoerter aus dem Katalog, Felder entfernen), damit die Regeln nicht je Aufrufer
    ''' auseinanderlaufen.
    '''
    ''' WOHIN: JPEG, PNG und WebP tragen die Aenderung IN der Datei; eine vorhandene Beistelldatei
    ''' zieht mit (nie neu angelegt), weil sie beim Lesen vorgeht. Alle anderen Formate bekommen sie
    ''' nur in die Beistelldatei; an eine RAW geht nichts direkt heran.
    '''
    ''' ALLES ODER NICHTS ueber beide Dateien: zuerst den alten Inhalt der Beistelldatei sichern und
    ''' sie schreiben (klein, atomar); scheitert das, ist nichts veraendert. Dann die Bilddatei
    ''' (atomar); scheitert DAS, wird die Beistelldatei Byte fuer Byte zurueckgesetzt. Andersherum
    ''' liesse sich ein schon geschriebenes Foto nur mit einer Kopie der ganzen Datei zuruecknehmen.
    '''
    ''' KEIN Ausweichen in die Beistelldatei, wenn die Bilddatei sich nicht schreiben laesst: sie
    ''' behielte ihren alten Wert, und jedes Programm, das nur in die Datei sieht, zeigte ihn weiter.
    ''' </summary>
    Public NotInheritable Class MetadataFileWriter

        Private Sub New()
        End Sub

        ''' <param name="writeIntoFile">Schreibt die Aenderung in die Bilddatei. True: geschrieben,
        ''' False: nichts zu aendern; jedes "geht nicht" als Ausnahme (siehe
        ''' ImageProcessor.EditXmpInFile).</param>
        ''' <param name="editSidecar">Die Aenderung an den rdf:Description-Knoten der
        ''' Beistelldatei (siehe ExifService.EditXmpSidecar).</param>
        ''' <param name="createsValues">Ob die Aenderung Werte SETZT. Nur dann darf fuer ein Format
        ''' ohne eingebettetes XMP eine neue Beistelldatei entstehen; eine Datei, die nur das Fehlen
        ''' von Feldern festhaelt, hat niemand verlangt.</param>
        ''' <param name="logName">Name im Protokoll.</param>
        Public Shared Function Write(imagePath As String,
                                     writeIntoFile As Func(Of Boolean),
                                     editSidecar As Func(Of IEnumerable(Of XElement), XElement, Boolean),
                                     createsValues As Boolean,
                                     logName As String) As MetadataWriteResult
            Dim result As New MetadataWriteResult()
            If String.IsNullOrWhiteSpace(imagePath) OrElse Not File.Exists(imagePath) Then Return result

            If ImageProcessor.CanWriteKeywordsInto(imagePath) Then
                result.Target = MetadataWriteTarget.InFile
                Dim existingSidecar = XmpSidecarService.FindSidecar(imagePath)
                Dim sidecarBefore As Byte() = Nothing
                If Not String.IsNullOrEmpty(existingSidecar) Then
                    Try
                        sidecarBefore = File.ReadAllBytes(existingSidecar)
                    Catch ex As Exception
                        DiagnosticLogService.LogException(logName & ".ReadSidecar", ex)
                        Return result
                    End Try
                    If String.IsNullOrEmpty(ExifService.EditXmpSidecar(imagePath, createIfMissing:=False, edit:=editSidecar)) Then
                        DiagnosticLogService.LogAlways(logName & ".SyncSidecar",
                                                       "Beistelldatei nicht geschrieben, Bild unveraendert: " & imagePath)
                        Return result
                    End If
                End If

                Try
                    If writeIntoFile() Then ExifService.Invalidate(imagePath)
                Catch ex As Exception
                    DiagnosticLogService.LogException(logName & ".WriteIntoFile", ex)
                    If sidecarBefore IsNot Nothing AndAlso Not ExifService.RestoreXmpSidecar(existingSidecar, sidecarBefore) Then
                        DiagnosticLogService.LogAlways(logName & ".SyncSidecar",
                                                       "Bild nicht geschrieben und Beistelldatei nicht zurueckgesetzt, sie traegt den neuen Stand: " & existingSidecar)
                    End If
                    Return result
                End Try
                result.Success = True
                Return result
            End If

            result.Target = MetadataWriteTarget.XmpSidecar
            If Not createsValues AndAlso String.IsNullOrEmpty(XmpSidecarService.FindSidecar(imagePath)) Then
                result.Success = True
                Return result
            End If
            result.Success = Not String.IsNullOrEmpty(ExifService.EditXmpSidecar(imagePath, createIfMissing:=createsValues, edit:=editSidecar))
            Return result
        End Function

    End Class

End Namespace
