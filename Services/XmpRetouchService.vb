Imports System
Imports System.Collections.Generic
Imports System.Globalization
Imports System.IO
Imports System.Linq
Imports System.Text.RegularExpressions
Imports System.Xml.Linq

Namespace Services

    ''' <summary>Die Retusche eines Fotos aus seinen fremden Entwicklungseinstellungen: die
    ''' Ausbesserungsstellen des Referenzprogramms (crs:RetouchAreas, in alten Fassungen
    ''' crs:RetouchInfo) als Retuschestellen unseres Rezepts.
    '''
    ''' <para>NUR FUER DIE EIGENEN EINSTELLUNGEN EINES FOTOS, nie fuer ein Preset: eine Staubstelle
    ''' gehoert zu genau dem Bild, auf dem sie sitzt. Gerufen wird es deshalb nur von den beiden
    ''' Uebernahmen in eine neue .fpxmp (RawSidecarService.TryImportFromXmpSidecar und
    ''' TryImportFromForeignCatalog), nicht von LoadLook, das auch Presets liest.</para>
    '''
    ''' <para>DIE LAGE. Alle Stellen stehen als Bruchteile im UNGEDREHTEN Sensorrahmen, also vor
    ''' der Drehung, die die Kamera vermerkt hat; Radien als Bruchteil der langen Kante. Unser
    ''' Rezept zaehlt in Pixeln des Bildes NACH dieser Drehung (SourceSpace, siehe
    ''' GEOMETRIE_UND_KOORDINATEN.md). Umgerechnet wird mit der Drehung, die der Decode selbst
    ''' anwendet (LibRaws flip), nicht mit einer zweiten Quelle dafuer.</para>
    '''
    ''' <para>DIE ARTEN. "clone" wird ein Stempel mit seiner Quelle, wie ihn der Editor anlegt
    ''' (Art "Blur" samt Quellpunkt). "heal" wird unsere Reparatur OHNE Quelle: sie fuellt die
    ''' Stelle aus ihrer Umgebung selbst, eine fremde Quelle kennt sie nicht. Ein Pinselbereich
    ''' wird je Tupfer eine Stelle mit gemeinsamer Strichnummer; die Reparatur fuellt ihn dann als
    ''' EINE Flaeche. Andere Arten (generatives Entfernen) und Formen ohne Gegenstueck fallen weg
    ''' und werden gezaehlt.</para>
    '''
    ''' <para>NICHT UEBERNOMMEN: die Rote-Augen-Korrektur (crs:RedEyeInfo). Unsere wird beim
    ''' Klicken ins Arbeitsbild gerechnet und steht nicht im Rezept; neben einer RAW haette sie
    ''' keinen Ort.</para></summary>
    Public NotInheritable Class XmpRetouchService

        Private Sub New()
        End Sub

        Private Shared ReadOnly Crs As XNamespace = "http://ns.adobe.com/camera-raw-settings/1.0/"
        Private Shared ReadOnly Rdf As XNamespace = "http://www.w3.org/1999/02/22-rdf-syntax-ns#"

        ''' <summary>Erste Strichnummer der uebernommenen Stellen. Der Editor zaehlt seine eigenen
        ''' je Sitzung ab 1; die Reparatur fasst aufeinanderfolgende Stellen mit GLEICHER Nummer zu
        ''' einer Flaeche zusammen. Mit Abstand dazu verschmilzt ein neuer Strich nie mit der
        ''' letzten uebernommenen Flaeche.</summary>
        Friend Const FirstImportedStrokeId As Integer = 1000000

        ''' <summary>Ein Punkt einer Stelle im Sensorrahmen: Lage als Bruchteil, Radius als
        ''' Bruchteil der langen Kante.</summary>
        Public Structure AreaPoint
            Public X As Double
            Public Y As Double
            Public Radius As Double
        End Structure

        ''' <summary>Eine Ausbesserungsstelle, wie sie gelesen wurde, noch ohne Bildmasse.</summary>
        Public NotInheritable Class AreaSpec
            Public IsClone As Boolean
            ''' <summary>Ein Punkt fuer einen Kreis, die Tupfer fuer einen Pinselbereich.</summary>
            Public ReadOnly Points As New List(Of AreaPoint)()
            ''' <summary>Die Quelle des ersten Punkts; die uebrigen haben denselben Versatz.</summary>
            Public SourceX As Double
            Public SourceY As Double
            Public HasSource As Boolean
            ''' <summary>Weiche Kante 0 bis 1.</summary>
            Public Feather As Double
            ''' <summary>Deckkraft 0 bis 1.</summary>
            Public Opacity As Double = 1.0
        End Class

        ''' <summary>Das Ergebnis des Lesens: die Stellen und wie viele wegfielen.</summary>
        Public NotInheritable Class ParseResult
            Public ReadOnly Areas As New List(Of AreaSpec)()
            Public Skipped As Integer
        End Class

        ''' <summary>Liest die Ausbesserungsstellen aus dem Text einer XMP. Steht RetouchAreas in der
        ''' Datei, gilt nur sie, AUCH LEER: eine leere Liste heisst "alle Stellen geloescht", und
        ''' eine daneben stehen gebliebene RetouchInfo braechte sie sonst zurueck. RetouchInfo ist
        ''' die Form von Fassungen davor und wird nur gelesen, wenn die neue ganz fehlt.</summary>
        Public Shared Function Parse(xmpText As String) As ParseResult
            Dim result As New ParseResult()
            If String.IsNullOrEmpty(xmpText) Then Return result
            If NamesBlock(xmpText, "RetouchAreas") Then
                Dim areas = ReadBlock(xmpText, "RetouchAreas")
                If areas Is Nothing Then Return result
                For Each item In areas
                    Dim spec = ReadArea(item)
                    If spec Is Nothing Then result.Skipped += 1 Else result.Areas.Add(spec)
                Next
                Return result
            End If
            Dim info = ReadBlock(xmpText, "RetouchInfo")
            If info Is Nothing Then Return result
            For Each item In info
                Dim spec = ReadLegacySpot(item)
                If spec Is Nothing Then result.Skipped += 1 Else result.Areas.Add(spec)
            Next
            Return result
        End Function

        ''' <summary>Nennt die XMP diesen Block ueberhaupt, als Element (auch leer oder
        ''' selbstschliessend) oder als Attribut?</summary>
        Private Shared Function NamesBlock(xmpText As String, name As String) As Boolean
            Return Regex.IsMatch(xmpText, "<crs:" & name & "[\s/>]|\scrs:" & name & "\s*=")
        End Function

        ''' <summary>Die Eintraege der Liste eines Blocks (crs:Name/rdf:Seq/rdf:li), Nothing wenn
        ''' der Block fehlt oder sich nicht lesen laesst. Der Block wird herausgeschnitten und mit
        ''' den beiden Namensraeumen allein gelesen: ein ganzes XMP-Paket ist nicht immer
        ''' wohlgeformt, und ein Fehler anderswo soll die Retusche nicht mitnehmen.</summary>
        Private Shared Function ReadBlock(xmpText As String, name As String) As List(Of XElement)
            Dim m = Regex.Match(xmpText, "<crs:" & name & "(?:\s[^>]*)?(?<!/)>.*?</crs:" & name & ">", RegexOptions.Singleline)
            If Not m.Success Then Return Nothing
            Try
                Dim wrapped = "<w xmlns:crs=""" & Crs.NamespaceName & """ xmlns:rdf=""" & Rdf.NamespaceName & """>" & m.Value & "</w>"
                Dim block = XElement.Parse(wrapped).Element(Crs + name)
                Dim seq = block?.Element(Rdf + "Seq")
                If seq Is Nothing Then seq = block?.Element(Rdf + "Bag")
                If seq Is Nothing Then Return Nothing
                Return seq.Elements(Rdf + "li").ToList()
            Catch ex As Exception
                DiagnosticLogService.LogException("XmpRetouch.Read", ex)
                Return Nothing
            End Try
        End Function

        ''' <summary>Der Knoten, der die Felder eines Eintrags traegt: das li selbst (Attribute oder
        ''' rdf:parseType="Resource") oder die rdf:Description darin.</summary>
        Private Shared Function Body(li As XElement) As XElement
            Return If(li.Element(Rdf + "Description"), li)
        End Function

        ''' <summary>Ein Feld als Attribut oder als Kindelement ohne eigene Kinder.</summary>
        Private Shared Function Field(node As XElement, name As String) As String
            Dim attribute = node.Attribute(Crs + name)
            If attribute IsNot Nothing Then Return attribute.Value
            Dim child = node.Element(Crs + name)
            If child IsNot Nothing AndAlso Not child.HasElements Then Return child.Value
            Return Nothing
        End Function

        Private Shared Function Number(text As String, ByRef value As Double) As Boolean
            If text Is Nothing Then Return False
            Return Double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, value) AndAlso
                   Double.IsFinite(value)
        End Function

        Private Shared Function Number(node As XElement, name As String, ByRef value As Double) As Boolean
            Return Number(Field(node, name), value)
        End Function

        Private Shared Function ReadArea(li As XElement) As AreaSpec
            Dim node = Body(li)
            Dim spec As New AreaSpec()
            Dim kind = If(Field(node, "SpotType"), "heal").Trim()
            If String.Equals(kind, "clone", StringComparison.OrdinalIgnoreCase) Then
                spec.IsClone = True
            ElseIf Not String.Equals(kind, "heal", StringComparison.OrdinalIgnoreCase) Then
                Return Nothing
            End If
            ' Die Quelle steht unter SourceX und OffsetY - beides ist die LAGE der Quelle im
            ' Sensorrahmen, kein Versatz, trotz des Namens.
            spec.HasSource = Number(node, "SourceX", spec.SourceX) And Number(node, "OffsetY", spec.SourceY)
            If spec.IsClone AndAlso Not spec.HasSource Then Return Nothing
            Dim d As Double
            If Number(node, "Feather", d) Then spec.Feather = Clamp01(If(d > 1.0, d / 100.0, d))
            If Number(node, "Opacity", d) Then spec.Opacity = Clamp01(d)

            Dim masks = node.Element(Crs + "Masks")?.Element(Rdf + "Seq")
            Dim first = masks?.Elements(Rdf + "li").FirstOrDefault()
            If first Is Nothing Then Return Nothing
            Dim mask = Body(first)
            Dim what = If(Field(mask, "What"), "")
            Select Case what
                Case "Mask/Ellipse", "Mask/Circle"
                    Dim p As AreaPoint
                    If Not Number(mask, "X", p.X) OrElse Not Number(mask, "Y", p.Y) OrElse
                       Not Number(mask, "SizeX", p.Radius) Then Return Nothing
                    spec.Points.Add(p)
                Case "Mask/Paint"
                    If Not ReadDabs(mask, spec.Points) Then Return Nothing
                Case Else
                    Return Nothing
            End Select
            If spec.Points.Any(Function(p) p.Radius <= 0) Then Return Nothing
            Return spec
        End Function

        ''' <summary>Die Tupfer eines Pinselbereichs: "d x y" (aeltere Kataloge "M x y") setzt
        ''' einen, "r r" aendert den Radius fuer die folgenden.</summary>
        Private Shared Function ReadDabs(mask As XElement, points As List(Of AreaPoint)) As Boolean
            Dim radius As Double
            If Not Number(mask, "Radius", radius) Then Return False
            Dim dabs = mask.Element(Crs + "Dabs")?.Element(Rdf + "Seq")
            If dabs Is Nothing Then Return False
            For Each li In dabs.Elements(Rdf + "li")
                Dim parts = li.Value.Split({" "c, ChrW(9)}, StringSplitOptions.RemoveEmptyEntries)
                If parts.Length = 3 AndAlso (parts(0) = "d" OrElse parts(0) = "M") Then
                    Dim p As AreaPoint
                    If Not Number(parts(1), p.X) OrElse Not Number(parts(2), p.Y) Then Return False
                    p.Radius = radius
                    points.Add(p)
                ElseIf parts.Length = 2 AndAlso parts(0) = "r" Then
                    If Not Number(parts(1), radius) Then Return False
                Else
                    Return False
                End If
            Next
            Return points.Count > 0
        End Function

        ''' <summary>Die alte Form: je Stelle ein Text "centerX = 0.5, centerY = 0.4, radius = 0.02,
        ''' sourceState = ..., sourceX = ..., sourceY = ..., spotType = heal", oder dieselben
        ''' Felder als Attribute.</summary>
        Private Shared Function ReadLegacySpot(li As XElement) As AreaSpec
            Dim values As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)
            Dim node = Body(li)
            If Not node.HasElements AndAlso Not node.Attributes().Any(Function(a) a.Name.Namespace = Crs) Then
                For Each pair In node.Value.Split(","c)
                    Dim eq = pair.IndexOf("="c)
                    If eq > 0 Then values(pair.Substring(0, eq).Trim()) = pair.Substring(eq + 1).Trim()
                Next
            Else
                For Each name In {"centerX", "centerY", "radius", "sourceX", "sourceY", "spotType", "opacity"}
                    Dim v = Field(node, name)
                    If v IsNot Nothing Then values(name) = v
                Next
            End If
            Dim get_ = Function(k As String) As String
                           Dim v As String = Nothing
                           Return If(values.TryGetValue(k, v), v, Nothing)
                       End Function
            Dim spec As New AreaSpec()
            Dim p As AreaPoint
            If Not Number(get_("centerX"), p.X) OrElse Not Number(get_("centerY"), p.Y) OrElse
               Not Number(get_("radius"), p.Radius) OrElse p.Radius <= 0 Then Return Nothing
            spec.Points.Add(p)
            spec.IsClone = String.Equals(get_("spotType"), "clone", StringComparison.OrdinalIgnoreCase)
            spec.HasSource = Number(get_("sourceX"), spec.SourceX) And Number(get_("sourceY"), spec.SourceY)
            If spec.IsClone AndAlso Not spec.HasSource Then Return Nothing
            Dim d As Double
            If Number(get_("opacity"), d) Then spec.Opacity = Clamp01(d)
            Return spec
        End Function

        ''' <summary>Die Stellen in Pixeln des Bildes, wie der Decode es ausgibt.</summary>
        ''' <param name="width">Breite des ausgegebenen (gedrehten) Bildes.</param>
        ''' <param name="height">Hoehe des ausgegebenen Bildes.</param>
        ''' <param name="flip">LibRaws flip: Bit 1 spiegelt die Spalten, Bit 2 die Zeilen, Bit 4
        ''' tauscht die Achsen (dieselbe Lesart wie RawDecodeService.OrientEdge).</param>
        Public Shared Function ToSpots(areas As IEnumerable(Of AreaSpec), width As Integer, height As Integer,
                                       flip As Integer) As List(Of RetouchSpot)
            Dim spots As New List(Of RetouchSpot)()
            If areas Is Nothing OrElse width < 2 OrElse height < 2 Then Return spots
            If flip < 0 OrElse flip > 7 Then flip = 0
            Dim longEdge = CDbl(Math.Max(width, height))
            Dim map = Function(u As Double, v As Double) As (X As Single, Y As Single)
                          If (flip And 1) <> 0 Then u = 1.0 - u
                          If (flip And 2) <> 0 Then v = 1.0 - v
                          If (flip And 4) <> 0 Then Return (CSng(v * width), CSng(u * height))
                          Return (CSng(u * width), CSng(v * height))
                      End Function
            Dim strokeId = FirstImportedStrokeId
            For Each area In areas
                If area Is Nothing OrElse area.Points.Count = 0 Then Continue For
                Dim anchor = map(area.Points(0).X, area.Points(0).Y)
                Dim source = If(area.HasSource, map(area.SourceX, area.SourceY), anchor)
                Dim dx = source.X - anchor.X, dy = source.Y - anchor.Y
                For Each p In area.Points
                    Dim at = map(p.X, p.Y)
                    Dim spot As New RetouchSpot With {
                        .XPixels = at.X, .YPixels = at.Y,
                        .RadiusPixels = CSng(Math.Max(1.0, p.Radius * longEdge)),
                        .OpacityPercent = CSng(area.Opacity * 100.0),
                        .HardnessPercent = CSng((1.0 - area.Feather) * 100.0),
                        .StrokeId = strokeId
                    }
                    If area.IsClone Then
                        ' Wie der Stempel des Editors: Art "Blur" mit Quellpunkt.
                        spot.Mode = "Blur"
                        spot.SourceXPixels = Math.Max(0.0F, at.X + dx)
                        spot.SourceYPixels = Math.Max(0.0F, at.Y + dy)
                    Else
                        spot.Mode = "Heal"
                    End If
                    spots.Add(spot)
                Next
                strokeId += 1
            Next
            Return spots
        End Function

        ''' <summary>Haengt die Retusche aus <paramref name="xmpText"/> an ein frisch uebernommenes
        ''' Rezept. Die Bildmasse kommen aus der Datei selbst, ohne Decode; ohne sie bleibt das Rezept
        ''' ohne Retusche statt mit falsch liegender. Setzt die Quellmasse des Rezepts, wenn sie noch
        ''' fehlen: an ihnen haengt, in welchem Pixelraum die Stellen liegen.</summary>
        ''' <returns>Die Zahl der uebernommenen Stellen (Kreise und Pinselbereiche).</returns>
        Public Shared Function AddToRecipe(look As ImageAdjustments, xmpText As String, imagePath As String) As Integer
            If look Is Nothing OrElse String.IsNullOrEmpty(xmpText) Then Return 0
            Dim parsed = Parse(xmpText)
            If parsed.Areas.Count = 0 Then
                If parsed.Skipped > 0 Then
                    DiagnosticLogService.LogAlways("XmpRetouch", $"{parsed.Skipped} Stellen ohne Gegenstueck: {Path.GetFileName(imagePath)}")
                End If
                Return 0
            End If
            Dim frame = FrameOf(imagePath)
            If frame.Width < 2 Then
                DiagnosticLogService.LogAlways("XmpRetouch", $"keine Bildmasse fuer {Path.GetFileName(imagePath)}, Retusche nicht uebernommen")
                Return 0
            End If
            If look.SourceWidthPixels < 2 OrElse look.SourceHeightPixels < 2 Then
                look.SourceWidthPixels = frame.Width
                look.SourceHeightPixels = frame.Height
            End If
            ' Liegt der Rezeptraum schon anders (ein Ausgangsrezept mit eigenen Massen), werden die
            ' Stellen in ihm angelegt und nicht im Raum der Datei.
            Dim spots = ToSpots(parsed.Areas, look.SourceWidthPixels, look.SourceHeightPixels, frame.Flip)
            If look.RetouchSpots Is Nothing Then look.RetouchSpots = New List(Of RetouchSpot)()
            look.RetouchSpots.AddRange(spots)
            DiagnosticLogService.LogAlways("XmpRetouch",
                $"{parsed.Areas.Count} Stellen ({spots.Count} Punkte) uebernommen, {parsed.Skipped} ohne Gegenstueck: {Path.GetFileName(imagePath)}")
            Return parsed.Areas.Count
        End Function

        ''' <summary>Masse des ausgegebenen Bildes und die Drehung dahin. Eine RAW fragt LibRaw ohne
        ''' Decode; alles andere hat keine eigene Drehung im Sensorrahmen.</summary>
        Private Shared Function FrameOf(imagePath As String) As (Width As Integer, Height As Integer, Flip As Integer)
            If String.IsNullOrWhiteSpace(imagePath) Then Return (0, 0, 0)
            Dim file = VariantKey.FileOf(imagePath)
            Try
                If RawPreviewService.IsSupportedRaw(file) Then
                    Dim facts = RawDecodeService.ReadFileMetadata(file)
                    If facts Is Nothing OrElse facts.Flip < 0 OrElse facts.OrientedWidth < 2 OrElse facts.OrientedHeight < 2 Then Return (0, 0, 0)
                    Return (facts.OrientedWidth, facts.OrientedHeight, facts.Flip)
                End If
                Dim size = ExifService.ReadImageDimensions(file)
                If size.Width.HasValue AndAlso size.Height.HasValue Then Return (size.Width.Value, size.Height.Value, 0)
            Catch ex As Exception
                DiagnosticLogService.LogException("XmpRetouch.Frame", ex)
            End Try
            Return (0, 0, 0)
        End Function

        Private Shared Function Clamp01(value As Double) As Double
            Return Math.Max(0.0, Math.Min(1.0, value))
        End Function

    End Class

End Namespace
