Imports System
Imports System.Collections.Generic
Imports System.Globalization
Imports System.IO
Imports System.Linq
Imports System.Xml.Linq

Namespace Services

    ''' <summary>Eine Gesichtsregion, wie sie in einer XMP-Beistelldatei steht.</summary>
    Public Class FaceRegion
        Public Property Name As String = ""
        ''' <summary>Mittelpunkt und Groesse, RELATIV zum Bild (0 bis 1). So verlangt es der
        ''' Standard - und nur so ueberlebt die Angabe ein spaeteres Verkleinern des Bildes.</summary>
        Public Property CenterX As Double
        Public Property CenterY As Double
        Public Property Width As Double
        Public Property Height As Double
    End Class

    ''' <summary>Personen in die XMP-Beistelldatei schreiben und von dort lesen.
    '''
    ''' WARUM UEBERHAUPT: Ohne das bleiben die erkannten Personen in FerrumPix eingesperrt. Beim
    ''' Export, beim Wechsel zu einem anderen Programm oder beim Umziehen der Bibliothek waere die
    ''' Zuordnung weg. Ein Stichwort traegt zwar den Namen, aber nicht, WO im Bild jemand ist.
    '''
    ''' DER STANDARD ist mwg-rs (Metadata Working Group), den Lightroom, digiKam und Picasa
    ''' schreiben. Aufbau: unter <c>mwg-rs:Regions</c> steht eine <c>RegionList</c>, darin je Person
    ''' ein Eintrag mit Namen, Typ "Face" und einem Bereich aus Mittelpunkt und Groesse.
    '''
    ''' RELATIVE KOORDINATEN, nicht Pixel. Das ist keine Formalie: eine Region in Pixeln waere nach
    ''' dem ersten Verkleinern falsch, und andere Programme lesen sie ohnehin nur relativ. Umgerechnet
    ''' wird an genau EINER Stelle, hier.
    '''
    ''' GELESEN WIRD AUCH. Wer nur schreibt, verliert bei jedem Zyklus etwas: eine Zuordnung, die in
    ''' einem anderen Programm entstanden ist, kaeme nie an - und wer FerrumPix neu aufsetzt, faengt
    ''' bei null an, obwohl die Namen neben den Fotos liegen.</summary>
    Public NotInheritable Class XmpFaceRegionService

        Private Sub New()
        End Sub

        Private Shared ReadOnly RdfNs As XNamespace = "http://www.w3.org/1999/02/22-rdf-syntax-ns#"
        Private Shared ReadOnly MwgRsNs As XNamespace = "http://www.metadataworkinggroup.com/schemas/regions/"
        Private Shared ReadOnly StAreaNs As XNamespace = "http://ns.adobe.com/xmp/sType/Area#"
        Private Shared ReadOnly StDimNs As XNamespace = "http://ns.adobe.com/xap/1.0/sType/Dimensions#"

        ''' <summary>Liest die Gesichtsregionen aus einer Beistelldatei. Leere Liste, wenn keine da
        ''' sind oder die Datei nicht lesbar ist - eine fehlende Angabe ist kein Fehler.</summary>
        Public Shared Function ReadRegions(sidecarPath As String) As List(Of FaceRegion)
            Dim result As New List(Of FaceRegion)()
            If String.IsNullOrWhiteSpace(sidecarPath) OrElse Not File.Exists(sidecarPath) Then Return result
            Try
                Dim doc = XDocument.Load(sidecarPath)
                For Each area In doc.Descendants(MwgRsNs + "RegionList").Descendants(RdfNs + "li")
                    Dim typ = ReadField(area, MwgRsNs + "Type")
                    ' Nur Gesichter. Derselbe Aufbau traegt auch Haustiere und Sehenswuerdigkeiten.
                    If typ.Length > 0 AndAlso Not typ.Equals("Face", StringComparison.OrdinalIgnoreCase) Then Continue For

                    Dim name = ReadField(area, MwgRsNs + "Name")
                    If String.IsNullOrWhiteSpace(name) Then Continue For

                    Dim region As New FaceRegion With {.Name = name.Trim()}
                    Dim bereich = area.Descendants(MwgRsNs + "Area").FirstOrDefault()
                    If bereich IsNot Nothing Then
                        region.CenterX = ReadNumber(bereich, StAreaNs + "x")
                        region.CenterY = ReadNumber(bereich, StAreaNs + "y")
                        region.Width = ReadNumber(bereich, StAreaNs + "w")
                        region.Height = ReadNumber(bereich, StAreaNs + "h")
                    End If
                    result.Add(region)
                Next
            Catch ex As Exception
                DiagnosticLogService.LogException("Xmp.RegionenLesen", ex)
            End Try
            Return result
        End Function

        ''' <summary>Schreibt die Regionen in eine Beistelldatei. Vorhandene Regionen werden ERSETZT,
        ''' alles andere in der Datei bleibt unangetastet - dort stehen Bewertung, Stichworte und
        ''' Entwicklungseinstellungen, die niemand verlieren will.
        '''
        ''' Ohne Namen keine Region: eine Gruppe, die noch "Ohne Namen" heisst, hat in einer Datei
        ''' nichts zu suchen. Sie liesse sich dort nur von Hand wieder loswerden.</summary>
        Public Shared Function WriteRegions(sidecarPath As String,
                                            imageWidth As Integer, imageHeight As Integer,
                                            regions As IReadOnlyList(Of FaceRegion)) As Boolean
            If String.IsNullOrWhiteSpace(sidecarPath) Then Return False
            If imageWidth <= 0 OrElse imageHeight <= 0 Then Return False
            Try
                Dim doc As XDocument
                If File.Exists(sidecarPath) Then
                    doc = XDocument.Load(sidecarPath)
                Else
                    doc = NewSidecar()
                End If

                Dim description = doc.Descendants(RdfNs + "Description").FirstOrDefault()
                If description Is Nothing Then Return False

                ' Alte Regionen weg, damit eine Umbenennung nicht neben der alten steht.
                description.Elements(MwgRsNs + "Regions").Remove()

                Dim benannte = If(regions, New List(Of FaceRegion)()).
                               Where(Function(r) r IsNot Nothing AndAlso Not String.IsNullOrWhiteSpace(r.Name)).
                               ToList()
                If benannte.Count > 0 Then
                    Dim liste As New XElement(RdfNs + "Bag")
                    For Each r In benannte
                        liste.Add(New XElement(RdfNs + "li",
                            New XAttribute(RdfNs + "parseType", "Resource"),
                            New XElement(MwgRsNs + "Name", r.Name),
                            New XElement(MwgRsNs + "Type", "Face"),
                            New XElement(MwgRsNs + "Area",
                                New XAttribute(RdfNs + "parseType", "Resource"),
                                New XElement(StAreaNs + "x", Zahl(r.CenterX)),
                                New XElement(StAreaNs + "y", Zahl(r.CenterY)),
                                New XElement(StAreaNs + "w", Zahl(r.Width)),
                                New XElement(StAreaNs + "h", Zahl(r.Height)),
                                New XElement(StAreaNs + "unit", "normalized"))))
                    Next

                    description.Add(New XElement(MwgRsNs + "Regions",
                        New XAttribute(RdfNs + "parseType", "Resource"),
                        New XElement(MwgRsNs + "AppliedToDimensions",
                            New XAttribute(RdfNs + "parseType", "Resource"),
                            New XElement(StDimNs + "w", imageWidth.ToString(CultureInfo.InvariantCulture)),
                            New XElement(StDimNs + "h", imageHeight.ToString(CultureInfo.InvariantCulture)),
                            New XElement(StDimNs + "unit", "pixel")),
                        New XElement(MwgRsNs + "RegionList", liste)))
                End If

                ' Ueber eine Nachbardatei schreiben, nie direkt: ein abgebrochener Schreibvorgang
                ' liesse sonst eine halbe Beistelldatei zurueck - mitsamt Bewertung und Stichworten.
                Dim temp = sidecarPath & ".neu"
                doc.Save(temp)
                File.Move(temp, sidecarPath, overwrite:=True)
                Return True
            Catch ex As Exception
                DiagnosticLogService.LogException("Xmp.RegionenSchreiben", ex)
                Return False
            End Try
        End Function

        ''' <summary>Die Bildgroesse, auf die sich die gespeicherten Gesichtsrechtecke beziehen.
        '''
        ''' Das ist die GEDREHTE Groesse, nicht die im Dateikopf: der Gesichtsscan arbeitet auf dem
        ''' aufgerichteten Bild (siehe FaceScanRunner.DecodeForScan). Wer hier die rohe Kopfgroesse
        ''' naehme, teilte bei einem hochkant aufgenommenen Foto durch die falsche Kante - die Region
        ''' saesse quer.
        '''
        ''' Gelesen wird nur der KOPF, kein Decode: das laeuft ueber jedes Bild einer Person.</summary>
        Public Shared Function ReadOrientedPixelSize(imagePath As String) As (Width As Integer, Height As Integer)
            If String.IsNullOrWhiteSpace(imagePath) OrElse Not File.Exists(imagePath) Then Return (0, 0)
            Try
                Dim w = 0, h = 0
                Using codec = SkiaSharp.SKCodec.Create(imagePath)
                    If codec Is Nothing Then Return (0, 0)
                    w = codec.Info.Width
                    h = codec.Info.Height
                End Using
                If w <= 0 OrElse h <= 0 Then Return (0, 0)
                Select Case ImageOrientationService.ReadOrigin(imagePath)
                    Case SkiaSharp.SKEncodedOrigin.LeftTop, SkiaSharp.SKEncodedOrigin.RightTop,
                         SkiaSharp.SKEncodedOrigin.RightBottom, SkiaSharp.SKEncodedOrigin.LeftBottom
                        Return (h, w)
                End Select
                Return (w, h)
            Catch ex As Exception
                DiagnosticLogService.LogException("Xmp.RegionenBildgroesse", ex)
                Return (0, 0)
            End Try
        End Function

        ''' <summary>Rechnet ein gefundenes Gesicht in eine Region um. Die Werte werden auf 0 bis 1
        ''' geklemmt: ein Gesicht am Bildrand kann rechnerisch darueber hinausragen, und ein
        ''' negativer Mittelpunkt waere fuer jeden Leser Unsinn.</summary>
        Public Shared Function ToRegion(name As String, face As DetectedFace,
                                        imageWidth As Integer, imageHeight As Integer) As FaceRegion
            If face Is Nothing OrElse imageWidth <= 0 OrElse imageHeight <= 0 Then Return Nothing
            Return ToRegion(name, face.X, face.Y, face.Width, face.Height, imageWidth, imageHeight)
        End Function

        ''' <summary>Dasselbe aus einem gespeicherten Gesichtsrechteck der Bibliothek - dort liegen
        ''' die Werte als Zahlen, nicht als <c>DetectedFace</c>. EINE Umrechnung fuer beide Wege.</summary>
        Public Shared Function ToRegion(name As String,
                                        x As Double, y As Double, width As Double, height As Double,
                                        imageWidth As Integer, imageHeight As Integer) As FaceRegion
            If imageWidth <= 0 OrElse imageHeight <= 0 Then Return Nothing
            Return New FaceRegion With {
                .Name = If(name, ""),
                .CenterX = Klemme((x + width / 2) / imageWidth),
                .CenterY = Klemme((y + height / 2) / imageHeight),
                .Width = Klemme(width / imageWidth),
                .Height = Klemme(height / imageHeight)}
        End Function

        Private Shared Function Klemme(v As Double) As Double
            If Double.IsNaN(v) Then Return 0
            Return Math.Max(0.0, Math.Min(1.0, v))
        End Function

        ''' <summary>Sechs Nachkommastellen reichen: bei einem 10000 Punkte breiten Bild ist das
        ''' noch ein Hundertstel Punkt. Immer mit Punkt als Trennzeichen - eine deutsche
        ''' Zahlenschreibweise waere fuer jeden anderen Leser kaputt.</summary>
        Private Shared Function Zahl(v As Double) As String
            Return v.ToString("0.######", CultureInfo.InvariantCulture)
        End Function

        Private Shared Function ReadField(parent As XElement, name As XName) As String
            Dim attribut = parent.Attribute(name)
            If attribut IsNot Nothing Then Return attribut.Value
            Dim element = parent.Descendants(name).FirstOrDefault()
            Return If(element Is Nothing, "", element.Value)
        End Function

        Private Shared Function ReadNumber(parent As XElement, name As XName) As Double
            Dim text = ReadField(parent, name)
            Dim wert As Double
            If Double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, wert) Then Return wert
            Return 0
        End Function

        Private Shared Function NewSidecar() As XDocument
            Return New XDocument(
                New XElement(XName.Get("xmpmeta", "adobe:ns:meta/"),
                    New XElement(RdfNs + "RDF",
                        New XElement(RdfNs + "Description",
                            New XAttribute(RdfNs + "about", "")))))
        End Function

    End Class

End Namespace
