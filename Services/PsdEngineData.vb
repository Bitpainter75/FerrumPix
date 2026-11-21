Imports System
Imports System.Collections.Generic
Imports System.Globalization
Imports System.Text

Namespace Services
    ''' <summary>Leser für die PostScript-artigen Textdaten in Photoshop-Textebenen.</summary>
    Public NotInheritable Class PsdEngineData
        Public Property FontSize As Double
        Public Property FontName As String = ""
        Public Property FillColor As String = ""
        ''' Zusätzlicher Zeichenabstand in Tausendsteln des Gevierts; NaN, wenn keiner dasteht.
        Public Property Tracking As Double = Double.NaN
        ''' Die Ausrichtung des ersten Absatzes: 0 links, 1 rechts, 2 Mitte, 3 bis 6 Blocksatz;
        ''' -1, wenn keine dasteht.
        Public Property Justification As Integer = -1
        ''' „Alle Großbuchstaben" (FontCaps 2). Kapitälchen (1) sind etwas anderes und bleiben aus.
        Public Property AllCaps As Boolean

        ''' <summary>Die Abbildung aus dem Textraum ins Dokument (xx, xy, yx, yy, tx, ty) und der
        ''' Rahmen im Textraum, beides aus dem TySh-Block (siehe <see cref="PsdTextReader.ExtractStyle"/>).
        ''' Nothing bzw. False, wenn sie fehlen oder nicht lesbar sind.</summary>
        Public Property Matrix As Double()
        Public Property HasBounds As Boolean
        Public Property BoundsLeft As Double
        Public Property BoundsTop As Double
        Public Property BoundsRight As Double
        Public Property BoundsBottom As Double

        ''' <summary>Die Nummer des Textobjekts im dokumentweiten Textblock (<c>TextIndex</c> des
        ''' TySh-Blocks); -1, wenn keine dasteht.</summary>
        Public Property TextIndex As Integer = -1
        ''' <summary>Der Text ist verformt (Bogen, Flagge, Stauchung): im TySh-Block steht eine
        ''' andere Verformung als "keine". Ein Textobjekt hat dafür kein Gegenstück.</summary>
        Public Property IsWarped As Boolean
        ''' <summary>Der Text läuft an einem Pfad. Das steht NICHT in der Ebene, dort sieht er aus
        ''' wie Rahmentext; die Kontur liegt im dokumentweiten Textblock (siehe
        ''' <see cref="PathTextIndices"/>), und <see cref="PsdLayerReader"/> trägt es hier nach.</summary>
        Public Property IsOnPath As Boolean

        ''' <summary>Warum ein Textobjekt diese Ebene nicht treu tragen kann, oder "", wenn es
        ''' kann (siehe <see cref="FindUnsupportedFormatting"/>). Nur für Protokoll und Probe; der
        ''' Anwender sieht nur die Zahl solcher Ebenen.</summary>
        Public Property UnsupportedReason As String = ""
        ''' <summary>Die Zeilen des Wortlauts, nach den harten Umbrüchen gezählt, ohne den
        ''' abschliessenden Umbruch, den Photoshop jedem Text anhängt; 0, wenn kein Wortlaut in den
        ''' Textdaten steht.</summary>
        Public Property LineCount As Integer
        ''' <summary>Der Zeilenabstand des Textes im Dokument (wie <see cref="FontSize"/> mit der
        ''' Matrix umgerechnet): fest eingestellt der Wert selbst, automatisch der Anteil des
        ''' Absatzes (Vorgabe 120 Prozent) mal Grad. NaN, wenn er nicht zu bestimmen ist.</summary>
        Public Property Leading As Double = Double.NaN

        Private Sub New()
        End Sub

        Public Shared Function Parse(data As Byte()) As PsdEngineData
            If data Is Nothing OrElse data.Length = 0 OrElse data.Length > 8_000_000 Then Return Nothing
            Try
                Dim parser As New Parser(data)
                Dim root = TryCast(parser.ReadValue(0), Dictionary(Of String, Object))
                If root Is Nothing Then Return Nothing
                Dim engine = Dict(root, "EngineDict")
                Dim run = Dict(engine, "StyleRun")
                Dim runs = Arr(run, "RunArray")
                If runs Is Nothing OrElse runs.Count = 0 Then Return Nothing
                ' Mehrere Stilabschnitte: es gilt der, der die meisten Zeichen deckt. Ein Textobjekt
                ' kennt nur EINE Schrift und einen Grad; ganz ohne Stil gingen aber auch Matrix und
                ' Rahmen verloren (PsdTextReader haengt sie an dieses Ergebnis), und der Text kaeme
                ' in der Vorgabeschrift, mit geschaetztem Grad, ungedreht und ohne Pruefung auf
                ' Streckung. Im Korpus traf das jede fuenfte Textebene, oft nur wegen eines
                ' abschliessenden Zeilenumbruchs in eigenem Abschnitt.
                Dim lengths = Arr(run, "RunLengthArray")
                Dim chosen = 0
                If lengths IsNot Nothing AndAlso lengths.Count = runs.Count Then
                    Dim longest = -1.0
                    For i = 0 To lengths.Count - 1
                        If TypeOf lengths(i) Is Double AndAlso CDbl(lengths(i)) > longest Then
                            longest = CDbl(lengths(i))
                            chosen = i
                        End If
                    Next
                End If
                Dim style = Dict(Dict(TryCast(runs(chosen), Dictionary(Of String, Object)), "StyleSheet"), "StyleSheetData")
                If style Is Nothing Then Return Nothing
                Dim result As New PsdEngineData()
                Dim size = Number(style, "FontSize")
                If size > 0 AndAlso size <= 100_000 Then result.FontSize = size
                result.AllCaps = Number(style, "FontCaps") = 2
                If style.ContainsKey("Tracking") AndAlso TypeOf style("Tracking") Is Double Then
                    Dim tracking = CDbl(style("Tracking"))
                    If Double.IsFinite(tracking) AndAlso Math.Abs(tracking) <= 10_000 Then result.Tracking = tracking
                End If
                Dim paragraphs = Arr(Dict(engine, "ParagraphRun"), "RunArray")
                If paragraphs IsNot Nothing AndAlso paragraphs.Count > 0 Then
                    Dim properties = Dict(Dict(TryCast(paragraphs(0), Dictionary(Of String, Object)), "ParagraphSheet"), "Properties")
                    Dim justification = Number(properties, "Justification")
                    If justification >= 0 AndAlso justification <= 6 AndAlso justification = Math.Truncate(justification) Then
                        result.Justification = CInt(justification)
                    End If
                End If
                Dim fonts = Arr(Dict(root, "ResourceDict"), "FontSet")
                result.UnsupportedReason = FindUnsupportedFormatting(root, engine, runs, lengths, fonts, result)
                Dim index = Number(style, "Font")
                If fonts IsNot Nothing AndAlso index >= 0 AndAlso index = Math.Truncate(index) AndAlso index < fonts.Count Then
                    Dim font = TryCast(fonts(CInt(index)), Dictionary(Of String, Object))
                    If font IsNot Nothing AndAlso font.ContainsKey("Name") Then result.FontName = TryCast(font("Name"), String)
                End If
                Dim values = Arr(Dict(style, "FillColor"), "Values")
                If values IsNot Nothing AndAlso values.Count = 4 Then
                    Dim c(3) As Integer
                    For i = 0 To 3
                        If Not TypeOf values(i) Is Double Then Return result
                        Dim v = CDbl(values(i))
                        If Double.IsNaN(v) OrElse v < 0 OrElse v > 1 Then Return result
                        c(i) = CInt(Math.Round(v * 255))
                    Next
                    result.FillColor = String.Format(CultureInfo.InvariantCulture, "#{0:X2}{1:X2}{2:X2}{3:X2}", c(0), c(1), c(2), c(3))
                End If
                Return result
            Catch
                Return Nothing
            End Try
        End Function

        ''' <summary>Prüft, ob ein Textobjekt diesen Text treu tragen kann, und liefert sonst den
        ''' ersten Grund ("" heißt: es kann). Nebenbei werden Zeilenzahl und Zeilenabstand gesetzt.
        '''
        ''' Ein Textobjekt trägt für den GANZEN Text: eine Schrift samt echtem Schnitt, einen Grad,
        ''' eine Farbe, „Alle Großbuchstaben", einen Zeichenabstand, einen Zeilenabstand und eine
        ''' Ausrichtung, gesetzt als
        ''' Punkttext ohne Umbruch, waagerecht. Alles andere hat dort kein Gegenstück und käme still
        ''' falsch: ein Textrahmen bräche nicht um, senkrechter Satz liefe waagerecht, und von
        ''' mehreren Stilläufen bliebe nur einer. Dann bleibt die Ebene das Bild, das Photoshop in
        ''' die Ebenendaten gelegt hat.
        '''
        ''' Verglichen werden die Läufe untereinander, nicht mit dem ersten allein. Läufe aus nichts
        ''' als Zeilenumbrüchen zählen nicht mit (Photoshop schließt jeden Text mit einem Umbruch in
        ''' eigenem Lauf ab), bei Läufen aus Leerraum die Farbe nicht, weil sie unsichtbar bleibt.
        ''' Was in einem Lauf fehlt, gilt aus dem Grundstil des Dokuments, sonst mit Photoshops
        ''' Vorgabe. Der Zeilenabstand muss in allen Läufen und Absätzen derselbe sein; ob das
        ''' Textobjekt ihn tragen kann, entscheidet der Import (<see cref="Leading"/>).</summary>
        Private Shared Function FindUnsupportedFormatting(root As Dictionary(Of String, Object),
                                                          engine As Dictionary(Of String, Object),
                                                          runs As List(Of Object), lengths As List(Of Object),
                                                          fonts As List(Of Object),
                                                          result As PsdEngineData) As String
            Dim shapes = Dict(Dict(engine, "Rendered"), "Shapes")
            ' Senkrechter Satz: Schreibrichtung 2. Ein Textobjekt setzt nur waagerecht.
            If Number(shapes, "WritingDirection") = 2 Then Return "vertical"
            Dim children = Arr(shapes, "Children")
            If children IsNot Nothing AndAlso children.Count > 0 Then
                ' Art 1 ist ein Textrahmen mit Umbruch an seiner Breite, 0 ein Punkttext. Der
                ' Text am Pfad ist ebenfalls Art 1, fällt aber schon vorher als verzerrt heraus.
                If Number(TryCast(children(0), Dictionary(Of String, Object)), "ShapeType") = 1 Then Return "paragraph"
            End If

            Dim text = TryCast(Value(Dict(engine, "Editor"), "Text"), String)
            If text Is Nothing Then text = ""
            Dim normalizedText = text.Replace(vbCrLf, vbCr).Replace(vbLf, vbCr)
            Dim trimmed = normalizedText.TrimEnd(ChrW(13), ChrW(0))
            result.LineCount = If(trimmed.Length = 0, 0, trimmed.Split(ChrW(13)).Length)
            Dim multiLine = result.LineCount > 1

            Dim resources = Dict(root, "ResourceDict")
            Dim normalStyle = Dict(SheetAt(resources, "StyleSheetSet", "TheNormalStyleSheet"), "StyleSheetData")
            Dim normalParagraph = Dict(SheetAt(resources, "ParagraphSheetSet", "TheNormalParagraphSheet"), "Properties")

            ' Die Absätze: eine Ausrichtung und ein automatischer Zeilenabstand für alle, keine
            ' Einzüge, kein Abstand zwischen Absätzen.
            Dim paragraphs = Arr(Dict(engine, "ParagraphRun"), "RunArray")
            Dim firstParagraph As Dictionary(Of String, Object) = Nothing
            If paragraphs IsNot Nothing Then
                For p = 0 To paragraphs.Count - 1
                    Dim properties = Dict(Dict(TryCast(paragraphs(p), Dictionary(Of String, Object)), "ParagraphSheet"), "Properties")
                    If p = 0 Then firstParagraph = properties
                    For Each key In {"FirstLineIndent", "StartIndent", "EndIndent"}
                        Dim indent = NumberOr(properties, normalParagraph, key, 0)
                        If Math.Abs(indent) > 0.001 Then Return "indent"
                    Next
                    If paragraphs.Count > 1 Then
                        For Each key In {"SpaceBefore", "SpaceAfter"}
                            If Math.Abs(NumberOr(properties, normalParagraph, key, 0)) > 0.001 Then Return "paragraph-spacing"
                        Next
                    End If
                    If p > 0 Then
                        For Each key In {"Justification", "AutoLeading"}
                            If Not SameValue(ValueOr(properties, normalParagraph, key), ValueOr(firstParagraph, normalParagraph, key)) Then Return "paragraphs:" & key
                        Next
                    End If
                Next
            End If
            ' Blocksatz streckt im Textobjekt jede Zeile außer der letzten auf die Rahmenbreite;
            ' ein Punkttext in Photoshop kennt keine Rahmenbreite und streckt nichts.
            If multiLine AndAlso NumberOr(firstParagraph, normalParagraph, "Justification", 0) >= 3 Then Return "justify"

            ' Die Stilläufe, nur die mit sichtbarem Inhalt.
            Dim reference As Dictionary(Of String, Object) = Nothing
            Dim referenceColor As Object = Nothing
            Dim haveColor = False
            Dim offset = 0
            For i = 0 To runs.Count - 1
                Dim segment = ""
                If lengths IsNot Nothing AndAlso lengths.Count = runs.Count AndAlso TypeOf lengths(i) Is Double Then
                    Dim length = CInt(Math.Max(0, Math.Min(CDbl(lengths(i)), normalizedText.Length - offset)))
                    segment = normalizedText.Substring(offset, length)
                    offset += length
                Else
                    segment = "?"
                End If
                If segment.Length > 0 AndAlso segment.Trim(ChrW(13), ChrW(0)).Length = 0 Then Continue For
                Dim data = Dict(Dict(TryCast(runs(i), Dictionary(Of String, Object)), "StyleSheet"), "StyleSheetData")
                If data Is Nothing Then data = New Dictionary(Of String, Object)()

                ' Was ein Textobjekt gar nicht kennt, auch nicht für den ganzen Text.
                For Each key In {"FauxBold", "FauxItalic"}
                    If IsTrue(ValueOr(data, normalStyle, key)) Then Return "faux"
                Next
                For Each key In {"Underline", "Strikethrough"}
                    If IsTrue(ValueOr(data, normalStyle, key)) Then Return "underline"
                Next
                If IsTrue(ValueOr(data, normalStyle, "StrokeFlag")) Then Return "stroke"
                If IsFalse(ValueOr(data, normalStyle, "FillFlag")) Then Return "nofill"
                For Each key In {"HorizontalScale", "VerticalScale"}
                    If Math.Abs(NumberOr(data, normalStyle, key, 1) - 1.0) > 0.001 Then Return "scale"
                Next
                If Math.Abs(NumberOr(data, normalStyle, "BaselineShift", 0)) > 0.001 Then Return "baseline"
                If NumberOr(data, normalStyle, "FontBaseline", 0) <> 0 Then Return "baseline"
                ' Kapitälchen (1); „Alle Großbuchstaben" (2) trägt das Textobjekt.
                If NumberOr(data, normalStyle, "FontCaps", 0) = 1 Then Return "smallcaps"
                ' Der Zeichenabstand des Textobjekts reicht von -20 bis 200 Prozent des Grads.
                Dim tracking = NumberOr(data, normalStyle, "Tracking", 0)
                If tracking < -200 OrElse tracking > 2000 Then Return "tracking"

                Dim autoLeading = Not IsFalse(ValueOr(data, normalStyle, "AutoLeading"))
                Dim fontSize = NumberOr(data, normalStyle, "FontSize", -1)
                If reference Is Nothing Then
                    reference = data
                    Dim factor = NumberOr(firstParagraph, normalParagraph, "AutoLeading", 1.2)
                    Dim leading = If(autoLeading, factor * fontSize, NumberOr(data, normalStyle, "Leading", -1))
                    If fontSize > 0 AndAlso leading > 0 AndAlso Double.IsFinite(leading) Then result.Leading = leading
                Else
                    For Each key In {"FontSize", "FontCaps", "Tracking", "AutoLeading"}
                        If Not SameValue(ValueOr(data, normalStyle, key), ValueOr(reference, normalStyle, key)) Then Return "runs:" & key
                    Next
                    If Not autoLeading AndAlso
                       Not SameValue(ValueOr(data, normalStyle, "Leading"), ValueOr(reference, normalStyle, "Leading")) Then Return "runs:Leading"
                    ' Die Schrift über ihren Namen: zwei Einträge im Schriftsatz können dieselbe
                    ' meinen.
                    If Not String.Equals(FontNameAt(fonts, ValueOr(data, normalStyle, "Font")),
                                         FontNameAt(fonts, ValueOr(reference, normalStyle, "Font")), StringComparison.Ordinal) Then Return "runs:Font"
                End If
                If segment.Trim().Length > 0 Then
                    Dim color = ValueOr(data, normalStyle, "FillColor")
                    If Not haveColor Then
                        referenceColor = color
                        haveColor = True
                    ElseIf Not SameValue(color, referenceColor) Then
                        Return "runs:FillColor"
                    End If
                End If
            Next
            Return ""
        End Function

        ''' Der Eintrag eines Stilsatzes, auf den der Grundstil des Dokuments zeigt.
        Private Shared Function SheetAt(resources As Dictionary(Of String, Object), setKey As String, indexKey As String) As Dictionary(Of String, Object)
            Dim sheets = Arr(resources, setKey)
            Dim index = Number(resources, indexKey)
            If sheets Is Nothing OrElse index < 0 OrElse index >= sheets.Count OrElse index <> Math.Truncate(index) Then Return Nothing
            Return TryCast(sheets(CInt(index)), Dictionary(Of String, Object))
        End Function

        Private Shared Function FontNameAt(fonts As List(Of Object), index As Object) As String
            If fonts Is Nothing OrElse Not TypeOf index Is Double Then Return ""
            Dim i = CDbl(index)
            If i < 0 OrElse i >= fonts.Count OrElse i <> Math.Truncate(i) Then Return ""
            Return If(TryCast(Value(TryCast(fonts(CInt(i)), Dictionary(Of String, Object)), "Name"), String), "")
        End Function

        Private Shared Function Value(d As Dictionary(Of String, Object), key As String) As Object
            If d Is Nothing OrElse Not d.ContainsKey(key) Then Return Nothing
            Return d(key)
        End Function

        ''' Der Wert aus dem Lauf, sonst aus dem Grundstil; Nothing, wenn beide schweigen.
        Private Shared Function ValueOr(d As Dictionary(Of String, Object), fallback As Dictionary(Of String, Object), key As String) As Object
            Dim v = Value(d, key)
            Return If(v, Value(fallback, key))
        End Function

        Private Shared Function NumberOr(d As Dictionary(Of String, Object), fallback As Dictionary(Of String, Object),
                                         key As String, defaultValue As Double) As Double
            Dim v = ValueOr(d, fallback, key)
            If TypeOf v Is Double AndAlso Double.IsFinite(CDbl(v)) Then Return CDbl(v)
            Return defaultValue
        End Function

        ''' Wahrheitswerte stehen als Wort da, nicht als Zahl.
        Private Shared Function IsTrue(v As Object) As Boolean
            Return String.Equals(TryCast(v, String), "true", StringComparison.Ordinal)
        End Function

        Private Shared Function IsFalse(v As Object) As Boolean
            Return String.Equals(TryCast(v, String), "false", StringComparison.Ordinal)
        End Function

        ''' Gleich nach Inhalt: Zahlen bis auf Rundung, Listen und Verzeichnisse Eintrag für Eintrag.
        Private Shared Function SameValue(a As Object, b As Object) As Boolean
            If a Is Nothing OrElse b Is Nothing Then Return a Is Nothing AndAlso b Is Nothing
            If TypeOf a Is Double AndAlso TypeOf b Is Double Then Return Math.Abs(CDbl(a) - CDbl(b)) <= 0.001
            Dim listA = TryCast(a, List(Of Object)), listB = TryCast(b, List(Of Object))
            If listA IsNot Nothing OrElse listB IsNot Nothing Then
                If listA Is Nothing OrElse listB Is Nothing OrElse listA.Count <> listB.Count Then Return False
                For i = 0 To listA.Count - 1
                    If Not SameValue(listA(i), listB(i)) Then Return False
                Next
                Return True
            End If
            Dim dictA = TryCast(a, Dictionary(Of String, Object)), dictB = TryCast(b, Dictionary(Of String, Object))
            If dictA IsNot Nothing OrElse dictB IsNot Nothing Then
                If dictA Is Nothing OrElse dictB Is Nothing OrElse dictA.Count <> dictB.Count Then Return False
                For Each kv In dictA
                    If Not dictB.ContainsKey(kv.Key) OrElse Not SameValue(kv.Value, dictB(kv.Key)) Then Return False
                Next
                Return True
            End If
            Return String.Equals(a.ToString(), b.ToString(), StringComparison.Ordinal)
        End Function

        ''' <summary>Welche Textobjekte des dokumentweiten Textblocks ("Txt2") an einem Pfad laufen,
        ''' als Menge ihrer Nummern (<see cref="TextIndex"/>). Leer, wenn keines oder der Block
        ''' nicht lesbar ist.
        '''
        ''' Der Block ist dieselbe Schreibweise wie die Textdaten einer Ebene, nur mit Nummern als
        ''' Schlüsseln und ohne äußere Klammer. Gebraucht werden drei Stellen: die Textobjekte
        ''' (<c>/1/1</c>, die Stelle in der Liste ist die Nummer), an jedem die Liste seiner Rahmen
        ''' (<c>/1/0</c>, je Eintrag die Nummer unter <c>/0</c>), und in der Rahmenliste (<c>/0/8/0</c>) die Art des Rahmens
        ''' (<c>/0/2/0</c>): ohne Eintrag Punkttext, 1 Rahmentext, 2 Text am Pfad.</summary>
        Public Shared Function PathTextIndices(data As Byte()) As HashSet(Of Integer)
            Return ReadDocumentText(data).OnPath
        End Function

        ''' <summary>Was der dokumentweite Textblock über die Textobjekte sagt, das in ihren Ebenen
        ''' nicht steht.</summary>
        Public NotInheritable Class DocumentTextFlags
            ''' Textobjekte an einem Pfad (siehe <see cref="PathTextIndices"/>).
            Public ReadOnly Property OnPath As New HashSet(Of Integer)()
            ''' <summary>Textobjekte mit einem Lauf, der nicht nach den Paarabständen der Schrift
            ''' unterschneidet: ohne Unterschneidung (0) oder optisch (2). Photoshop schreibt das
            ''' nur hierher; in den Textdaten der Ebene steht weiter „automatisch". Das Textobjekt
            ''' unterschneidet immer nach der Schrift.</summary>
            Public ReadOnly Property OtherKerning As New HashSet(Of Integer)()
        End Class

        ''' <summary>Liest beides aus dem Block in einem Durchgang. Die Stilläufe eines Textobjekts
        ''' liegen unter <c>/0/6/0</c>, je Lauf die Stilwerte unter <c>/0/0/6</c>; die Art der
        ''' Unterschneidung ist dort der Schlüssel <c>/11</c> (1 nach der Schrift). Fehlt er, gilt
        ''' die Vorgabe, also nach der Schrift. Leer, wenn der Block fehlt oder nicht lesbar ist.</summary>
        Public Shared Function ReadDocumentText(data As Byte()) As DocumentTextFlags
            Dim result As New DocumentTextFlags()
            If data Is Nothing OrElse data.Length = 0 OrElse data.Length > 32_000_000 Then Return result
            Try
                Dim wrapped(data.Length + 3) As Byte
                wrapped(0) = 60 : wrapped(1) = 60
                Buffer.BlockCopy(data, 0, wrapped, 2, data.Length)
                wrapped(wrapped.Length - 2) = 62 : wrapped(wrapped.Length - 1) = 62
                ' Der Block trägt je Zeile das fertig gesetzte Schriftbild; ein Dokument mit viel
                ' Text kommt damit weit über die Grenze, die für die Daten EINER Ebene reicht.
                Dim parser As New Parser(wrapped, 4_000_000)
                Dim root = TryCast(parser.ReadValue(0), Dictionary(Of String, Object))
                Dim frames = Arr(Dict(Dict(root, "0"), "8"), "0")
                Dim objects = Arr(Dict(root, "1"), "1")
                If objects Is Nothing Then Return result
                For i = 0 To objects.Count - 1
                    Dim textObject = TryCast(objects(i), Dictionary(Of String, Object))
                    Dim styleRuns = Arr(Dict(Dict(textObject, "0"), "6"), "0")
                    If styleRuns IsNot Nothing Then
                        For Each run In styleRuns
                            Dim values = Dict(Dict(Dict(TryCast(run, Dictionary(Of String, Object)), "0"), "0"), "6")
                            Dim kerning = Number(values, "11")
                            If kerning >= 0 AndAlso kerning <> 1 Then result.OtherKerning.Add(i)
                        Next
                    End If
                    If frames Is Nothing Then Continue For
                    Dim frameIndices = Arr(Dict(textObject, "1"), "0")
                    If frameIndices Is Nothing Then Continue For
                    For Each entry In frameIndices
                        ' Jeder Eintrag ist selbst ein Verzeichnis, die Nummer des Rahmens steht
                        ' darin unter /0.
                        Dim frameIndex = Number(TryCast(entry, Dictionary(Of String, Object)), "0")
                        If frameIndex < 0 OrElse frameIndex >= frames.Count OrElse frameIndex <> Math.Truncate(frameIndex) Then Continue For
                        Dim frame = Dict(Dict(TryCast(frames(CInt(frameIndex)), Dictionary(Of String, Object)), "0"), "2")
                        If Number(frame, "0") = 2 Then result.OnPath.Add(i)
                    Next
                Next
            Catch
                result = New DocumentTextFlags()
            End Try
            Return result
        End Function

        Private Shared Function Dict(d As Dictionary(Of String, Object), key As String) As Dictionary(Of String, Object)
            If d Is Nothing OrElse Not d.ContainsKey(key) Then Return Nothing
            Return TryCast(d(key), Dictionary(Of String, Object))
        End Function
        Private Shared Function Arr(d As Dictionary(Of String, Object), key As String) As List(Of Object)
            If d Is Nothing OrElse Not d.ContainsKey(key) Then Return Nothing
            Return TryCast(d(key), List(Of Object))
        End Function
        Private Shared Function Number(d As Dictionary(Of String, Object), key As String) As Double
            If d Is Nothing OrElse Not d.ContainsKey(key) OrElse Not TypeOf d(key) Is Double Then Return -1
            Return CDbl(d(key))
        End Function

        Private NotInheritable Class Parser
            Private ReadOnly bytes As Byte()
            Private pos As Integer
            Private count As Integer
            Private ReadOnly maxValues As Integer
            Public Sub New(data As Byte(), Optional maxValues As Integer = 100_000)
                bytes = data
                Me.maxValues = maxValues
            End Sub
            Private Sub Space()
                While pos < bytes.Length
                    If bytes(pos) = 37 Then
                        While pos < bytes.Length AndAlso bytes(pos) <> 10 AndAlso bytes(pos) <> 13
                            pos += 1
                        End While
                    ElseIf bytes(pos) <= 32 Then
                        pos += 1
                    Else
                        Exit While
                    End If
                End While
            End Sub
            Public Function ReadValue(depth As Integer) As Object
                count += 1
                If depth > 32 OrElse count > maxValues Then Throw New FormatException()
                Space()
                If pos >= bytes.Length Then Throw New FormatException()
                If bytes(pos) = 60 AndAlso pos + 1 < bytes.Length AndAlso bytes(pos + 1) = 60 Then
                    pos += 2
                    Dim d As New Dictionary(Of String, Object)(StringComparer.Ordinal)
                    Do
                        Space()
                        If pos + 1 < bytes.Length AndAlso bytes(pos) = 62 AndAlso bytes(pos + 1) = 62 Then
                            pos += 2
                            Return d
                        End If
                        If pos >= bytes.Length OrElse bytes(pos) <> 47 Then Throw New FormatException()
                        pos += 1
                        Dim key = Token()
                        d(key) = ReadValue(depth + 1)
                    Loop
                End If
                If bytes(pos) = 91 Then
                    pos += 1
                    Dim a As New List(Of Object)()
                    Do
                        Space()
                        If pos >= bytes.Length Then Throw New FormatException()
                        If bytes(pos) = 93 Then
                            pos += 1
                            Return a
                        End If
                        a.Add(ReadValue(depth + 1))
                    Loop
                End If
                If bytes(pos) = 40 Then Return ReadString()
                If bytes(pos) = 47 Then pos += 1
                Dim word = Token()
                Dim number As Double
                If Double.TryParse(word, NumberStyles.Float, CultureInfo.InvariantCulture, number) Then Return number
                Return word
            End Function
            Private Function Token() As String
                Dim start = pos
                While pos < bytes.Length AndAlso bytes(pos) > 32 AndAlso Array.IndexOf(New Byte() {47, 60, 62, 91, 93, 40, 41, 37}, bytes(pos)) < 0
                    pos += 1
                End While
                If pos = start Then Throw New FormatException()
                Return Encoding.ASCII.GetString(bytes, start, pos - start)
            End Function
            Private Function ReadString() As String
                pos += 1
                Dim result As New List(Of Byte)()
                Dim nesting = 1
                While pos < bytes.Length
                    Dim b = bytes(pos) : pos += 1
                    If b = 92 Then
                        If pos >= bytes.Length Then Throw New FormatException()
                        b = bytes(pos) : pos += 1
                    ElseIf b = 40 Then
                        nesting += 1
                    ElseIf b = 41 Then
                        nesting -= 1
                        If nesting = 0 Then
                            Dim raw = result.ToArray()
                            If raw.Length >= 2 AndAlso raw(0) = 254 AndAlso raw(1) = 255 Then Return Encoding.BigEndianUnicode.GetString(raw, 2, raw.Length - 2).TrimEnd(ChrW(0))
                            Return Encoding.UTF8.GetString(raw)
                        End If
                    End If
                    result.Add(b)
                End While
                Throw New FormatException()
            End Function
        End Class
    End Class
End Namespace
