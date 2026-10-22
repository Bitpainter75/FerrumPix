Imports System
Imports System.Collections.Generic
Imports System.IO
Imports System.IO.Compression
Imports System.Text
Imports SkiaSharp

Namespace Services

    ''' <summary>
    ''' Liest die EBENEN einer Photoshop-Datei, die PsdPreviewService bewusst überspringt. Damit lässt
    ''' sich eine .psd als Ebenenstapel öffnen, statt nur als fertiges Gesamtbild.
    '''
    ''' Was hier herauskommt, sind Bildebenen: Rechteck, Name, Deckkraft, Mischmethode, Beschneidung
    ''' an der Ebene darunter, Sichtbarkeit, die Ebenenmaske und die Bildpunkte selbst. Dazu die
    ''' Gruppen, in denen sie liegen. Mehr gibt das Format an dieser Stelle nicht her - und mehr
    ''' braucht es auch nicht, denn genau darauf bildet der eigene Ebenenstapel ab.
    '''
    ''' Kanäle liegen unkomprimiert, als RLE oder als ZIP vor. Alle drei werden gelesen; beim ZIP
    ''' gibt es zwei Spielarten, die zweite legt jeden Punkt als Differenz zu seinem linken Nachbarn
    ''' ab (siehe <see cref="ReadZipPlane"/>).
    '''
    ''' Acht und sechzehn Bit je Kanal, RGB, Graustufen und CMYK. Sechzehn Bit werden auf acht
    ''' gebracht, wie es der flache Weg seit jeher tut - der Ebenenstapel rechnet in acht Bit.
    '''
    ''' Gruppen sind im Format kein Behälter, sondern eine KLAMMER aus zwei Ebenen ohne Bildpunkte.
    ''' Sie werden als solche weitergereicht (<see cref="PsdLayerInfo.SectionType"/>); wer daraus
    ''' eine Struktur baut, ist der Import.
    '''
    ''' Bewusst nicht gelesen:
    ''' - Korrekturebenen. Photoshop legt für sie KEINE gerechneten Bildpunkte ab,
    '''   nur ihre Einstellungen in je eigenen, kaum dokumentierten Datensätzen. Sie kämen als leere
    '''   Ebene heraus und würden ein falsches Bild ergeben, deshalb bleiben sie draußen. Ihr Beitrag
    '''   steckt im Gesamtbild, das der Aufrufer als Grundlage nimmt.
    ''' - Vektormasken als Pfad. Bei einer gewöhnlichen Ebene hat Photoshop sie meist schon in
    '''   Kanal -2 gerastert, und die kommt mit; trägt die Ebene daneben eine gemalte Maske, kommt
    '''   die gemalte aus Kanal -3 (siehe SelectMaskChannel).
    ''' - Ebeneneffekte. Erkannt werden sie (<see cref="PsdLayerInfo.HasEffects"/>), nachgebildet
    '''   nicht.
    ''' - 32 Bit je Kanal. Dann liefert der Leser Nothing, und der Aufrufer bleibt beim flachen
    '''   Gesamtbild, das diese Variante besser auffangen kann.
    ''' </summary>
    Public NotInheritable Class PsdLayerReader

        Private Sub New()
        End Sub

        ''' Obergrenze für die Summe aller Ebenenflächen. Eine Photoshop-Datei mit hundert Ebenen in
        ''' voller Größe belegt sonst mehr Speicher, als sinnvoll zu halten ist.
        Private Const MaxTotalLayerPixels As Long = 400_000_000L
        ''' Obergrenze der Ebenenzahl, wie Photoshop sie selbst setzt. Den Speicher schuetzt die
        ''' Flaechengrenze darueber; hier stand frueher 300, und eine Datei mit vielen kleinen Ebenen
        ''' (Gruppenmarken zaehlen mit) ging dadurch flach auf, obwohl sie kaum Platz brauchte.
        Private Const MaxLayers As Integer = 8000

        Public Class PsdLayerInfo
            Public Property Name As String = ""
            Public Property Left As Integer
            Public Property Top As Integer
            Public Property Width As Integer
            Public Property Height As Integer
            Public Property OpacityPercent As Single = 100
            ''' Mischmethode in der Schreibweise von FerrumPix, siehe PsdWriterService.
            Public Property BlendMode As String = "Normal"
            Public Property ClipToLayerBelow As Boolean = False
            Public Property IsVisible As Boolean = True
            ''' Die Bildpunkte der Ebene, so groß wie ihr Rechteck. Gehört dem Aufrufer.
            Public Property Pixels As SKBitmap
            ''' <summary>Eine gewöhnliche Bildebene ohne einen Punkt (kein Rechteck, Pixels ist
            ''' Nothing). Der Import macht daraus eine leere Malebene.</summary>
            Public Property IsEmptyLayer As Boolean
            ''' <summary>Der Wortlaut, wenn es eine Textebene ist, sonst "". Nur der Wortlaut - was
            ''' die Schrift angeht, siehe PsdTextReader.</summary>
            Public Property TextContent As String = ""
            Public Property TextStyle As PsdEngineData

            ''' <summary>Die Ebenenmaske als Alpha8-Raster, oder Nothing. Gehört dem Aufrufer.
            '''
            ''' Ihr Rechteck ist ein EIGENES und hat mit dem der Ebene nichts zu tun: eine Maske darf
            ''' kleiner sein, größer, versetzt. Deshalb stehen die vier Werte hier noch einmal, statt
            ''' die der Ebene mitzubenutzen.
            '''
            ''' Die Werte sind Deckung, wie überall: 255 zeigt die Ebene, 0 versteckt sie. Photoshop
            ''' meint dasselbe (Weiß zeigt, Schwarz versteckt), also wird nichts umgekehrt.</summary>
            Public Property MaskPixels As SKBitmap
            Public Property MaskLeft As Integer
            Public Property MaskTop As Integer
            Public Property MaskWidth As Integer
            Public Property MaskHeight As Integer

            ''' <summary>Die Deckung AUSSERHALB des Maskenrechtecks. Nicht wegzudenken: eine Maske,
            ''' die nur ein Loch in eine sonst sichtbare Ebene stanzt, steht als kleines Rechteck mit
            ''' Vorgabewert 255 in der Datei. Wer den Wert übergeht, versteckt die ganze Ebene bis auf
            ''' dieses Rechteck - also genau das Gegenteil.
            '''
            ''' Üblich sind 0 und 255, aber in der Datei steht ein ganzes Byte, und ein Wert dazwischen
            ''' heißt: außerhalb deckt die Ebene eben halb. Er wird deshalb unverändert übernommen.</summary>
            Public Property MaskDefaultValue As Byte = 0

            ''' <summary>Die Maske ist in Photoshop abgeschaltet. Sie bleibt erhalten und wirkt
            ''' nicht - dasselbe kennt der eigene Maskentyp als <c>IsDisabled</c>.</summary>
            Public Property MaskDisabled As Boolean
            Public Property MaskFeatherPixels As Single

            Public ReadOnly Property HasMask As Boolean
                Get
                    Return MaskPixels IsNot Nothing AndAlso MaskWidth > 0 AndAlso MaskHeight > 0
                End Get
            End Property

            ''' <summary>0 für eine gewöhnliche Ebene, sonst eine GRUPPENMARKE: 1 ist eine offene
            ''' Gruppe, 2 eine zugeklappte, 3 das untere Ende einer Gruppe.
            '''
            ''' Die Reihenfolge in der Datei geht von UNTEN nach oben, und eine Gruppe steht darin
            ''' verkehrt herum: erst das Ende (3), dann ihr Inhalt, dann die Zeile mit Namen,
            ''' Deckkraft und Mischmethode (1 oder 2). Wer die Struktur nachbaut, öffnet also bei 3
            ''' und schließt bei 1 oder 2 - nicht umgekehrt.</summary>
            Public Property SectionType As Integer = 0

            Public ReadOnly Property IsGroupMarker As Boolean
                Get
                    Return SectionType <> 0
                End Get
            End Property

            ''' <summary>Die Ebene trägt eingeschaltete Ebeneneffekte (Schatten, Kontur, Schein
            ''' und so fort). Nachgebildet werden sie nicht: die Ebene kommt ohne sie an, und eine
            ''' Ebene mit Füllkraft 0, die nur über ihre Kontur zu sehen sein soll, bleibt
            ''' unsichtbar. Der Import meldet das deshalb.</summary>
            Public Property HasEffects As Boolean

            ''' <summary>Photoshops Sperren der Position und der transparenten Bildpunkte (Block
            ''' lspf, „alles sperren“ zählt für beide). Die dritte, „Bildpunkte sperren“, hat bei
            ''' FerrumPix kein Gegenstück.</summary>
            Public Property LockPosition As Boolean
            Public Property LockTransparency As Boolean
        End Class

        ''' <summary>Rückübersetzung der Vierzeichenschlüssel. Was hier fehlt, wird "Normal" - die
        ''' Bildpunkte stimmen dann, nur das Zusammenrechnen weicht ab.
        '''
        ''' Linear abwedeln ("lddg") ist die Addition und heißt im eigenen Renderer "Plus". Die
        ''' weiteren hier genannten Photoshop-Modi werden durch eigene Blender gerechnet.</summary>
        Private Shared ReadOnly BlendNames As New Dictionary(Of String, String)(StringComparer.Ordinal) From {
            {"norm", "Normal"}, {"mul ", "Multiply"}, {"scrn", "Screen"}, {"over", "Overlay"},
            {"dark", "Darken"}, {"lite", "Lighten"}, {"div ", "ColorDodge"}, {"idiv", "ColorBurn"},
            {"hLit", "HardLight"}, {"sLit", "SoftLight"}, {"diff", "Difference"}, {"smud", "Exclusion"},
            {"hue ", "Hue"}, {"sat ", "Saturation"}, {"colr", "Color"}, {"lum ", "Luminosity"},
            {"lddg", "Plus"}, {"lbrn", "LinearBurn"}, {"vLit", "VividLight"}, {"lLit", "LinearLight"},
            {"pLit", "PinLight"}, {"hMix", "HardMix"}, {"fsub", "Subtract"}, {"fdiv", "Divide"}
        }

        ''' <summary>Die Datei als Ganzes: Maße des Dokuments und die Ebenen darin.</summary>
        Public Class PsdDocumentInfo
            Public Property Width As Integer
            Public Property Height As Integer
            Public Property Layers As New List(Of PsdLayerInfo)()
            ''' <summary>Das Farbprofil des DOKUMENTS - es gilt fuer jede Ebene darin. Photoshop
            ''' legt seine Dateien oft in Adobe RGB ab; ohne diese Angabe kaemen die Ebenen als sRGB
            ''' an und stuenden flau und farbverschoben neben dem Gesamtbild, das laengst gewandelt
            ''' wird. Nothing heisst sRGB. Der Aufrufer gibt es frei.</summary>
            Public Property IccProfile As SKColorSpace
            ''' <summary>Wie viele Ebenen Bildpunkte tragen sollten, die sich nicht lesen ließen.
            ''' Sie fehlen im Stapel; der Rest der Datei kommt trotzdem.</summary>
            Public Property BrokenLayers As Integer
            ''' Sichtbare Anpassungsebenen, deren Wirkung beim Ebenenimport fehlt.
            Public Property AdjustmentLayers As Integer
            ''' <summary>Ob die Bildressourcen den eigenen Rezeptblock tragen, und beim vollen Lesen
            ''' sein Inhalt (siehe <see cref="PsdRecipeService.FindPayload"/>). Mitgelesen, weil der
            ''' Leser die Sektion ohnehin durchläuft; sonst öffnete der Import die Datei dafür eigens.</summary>
            Public Property HasRecipeBlock As Boolean
            Public Property RecipePayload As Byte()
            ''' <summary>Wie viele Datensätze das Ebenenverzeichnis nennt, auch die ohne Bildpunkte
            ''' (Korrekturebenen, Gruppenmarken) - dieselbe Zahl wie <see cref="CountLayerRecords"/>.</summary>
            Public Property LayerRecordCount As Integer
        End Class

        ''' <summary>Liefert die Bildebenen von unten nach oben, oder Nothing, wenn die Datei keine
        ''' Ebenen trägt oder in einer Spielart vorliegt, die dieser Leser nicht beherrscht.</summary>
        Public Shared Function ReadLayers(filePath As String) As List(Of PsdLayerInfo)
            Dim doc = ReadDocument(filePath)
            If doc Is Nothing Then Return Nothing
            ' Wer nur die Ebenen will, bekommt das Dokument nie in die Hand - und mit ihm nicht sein
            ' Farbprofil. Das ist natives Eigentum und haengt sonst bis zum Finalisierer am Speicher.
            doc.IccProfile?.Dispose()
            doc.IccProfile = Nothing
            Return doc.Layers
        End Function

        ''' <summary>Wie <see cref="ReadLayers"/>, liefert zusätzlich die Maße des Dokuments - der
        ''' Import braucht sie, weil eine Ebene kleiner sein kann als das Bild und der Ebenenstapel
        ''' trotzdem auf die richtige Fläche gehört.</summary>
        ''' <param name="metadataOnly">Nur das Verzeichnis lesen, die Bildpunkte überspringen. Für
        ''' die Frage "gibt es hier Textebenen?" - die kostet sonst das Entpacken jeder einzelnen
        ''' Ebene, und gleich darauf wird die Datei ohnehin richtig geladen.</param>
        Public Shared Function ReadDocument(filePath As String, Optional metadataOnly As Boolean = False) As PsdDocumentInfo
            Dim result As PsdDocumentInfo = Nothing
            Try
                Using fs = File.OpenRead(filePath)
                    result = ReadLayersCore(fs, metadataOnly)
                End Using
            Catch
                result = Nothing
            End Try

            If result IsNot Nothing AndAlso result.Layers.Count = 0 Then Return Nothing
            Return result
        End Function

        ''' <summary>Wie viele Ebenen im Verzeichnis der Datei stehen - einschliesslich derer, die
        ''' keine Bildpunkte tragen. Die Differenz zu den gelieferten Ebenen sind die
        ''' uebersprungenen: Korrekturebenen, Effekte, Gruppenmarken. 0, wenn sich das nicht
        ''' feststellen laesst - eine erfundene Zahl waere schlechter als keine.</summary>
        Public Shared Function CountLayerRecords(filePath As String) As Integer
            Try
                Using fs = File.OpenRead(filePath)
                    Dim buf(25) As Byte
                    If Not ReadExactly(fs, buf, 26) Then Return 0
                    If buf(0) <> Asc("8") OrElse buf(1) <> Asc("B") OrElse buf(2) <> Asc("P") OrElse buf(3) <> Asc("S") Then Return 0
                    Dim isPsb = (CInt(buf(4)) * 256 + buf(5)) = 2

                    If Not SkipBlock(fs, ReadU32(fs)) Then Return 0
                    If Not SkipBlock(fs, ReadU32(fs)) Then Return 0
                    Dim sectionLen = If(isPsb, ReadU64(fs), CLng(ReadU32(fs)))
                    If sectionLen <= 0 Then Return 0
                    Dim sectionEnd = fs.Position + sectionLen
                    If sectionEnd > fs.Length Then Return 0
                    If Not SeekLayerCount(fs, isPsb, sectionEnd) Then Return 0

                    Dim raw = ReadU16(fs)
                    If raw > 32767 Then raw -= 65536
                    Return Math.Abs(raw)
                End Using
            Catch
                Return 0
            End Try
        End Function

        Private Shared Function ReadLayersCore(fs As FileStream, Optional metadataOnly As Boolean = False) As PsdDocumentInfo
            Dim buf(25) As Byte
            If Not ReadExactly(fs, buf, 26) Then Return Nothing
            If buf(0) <> Asc("8") OrElse buf(1) <> Asc("B") OrElse buf(2) <> Asc("P") OrElse buf(3) <> Asc("S") Then Return Nothing

            Dim version = CInt(buf(4)) * 256 + buf(5)
            If version <> 1 AndAlso version <> 2 Then Return Nothing
            Dim isPsb = version = 2
            Dim docHeight = CInt((CLng(buf(14)) << 24) Or (CLng(buf(15)) << 16) Or (CLng(buf(16)) << 8) Or buf(17))
            Dim docWidth = CInt((CLng(buf(18)) << 24) Or (CLng(buf(19)) << 16) Or (CLng(buf(20)) << 8) Or buf(21))
            Dim depth = CInt(buf(22)) * 256 + buf(23)
            Dim colorMode = CInt(buf(24)) * 256 + buf(25)

            ' 8 und 16 Bit, RGB, Graustufen und CMYK. Sechzehn Bit werden dabei auf acht gebracht,
            ' wie es der flache Weg seit jeher tut - der Ebenenstapel rechnet in acht Bit, und ein
            ' Ebenenstapel mit etwas Genauigkeitsverlust ist mehr wert als gar keiner. CMYK nimmt
            ' denselben vorsichtigen, profilfreien Weg wie die Vorschau: die Ebenen bleiben damit
            ' editierbar, statt dass die Datei nur als Gesamtbild aufgeht.
            ' 32 Bit ist Gleitkomma und nur in RGB und Graustufen möglich; die Ebenen stehen dort in
            ' Lr32 und werden beim Lesen auf acht Bit gebracht (FloatSampleToByte).
            If depth <> 8 AndAlso depth <> 16 AndAlso depth <> 32 Then Return Nothing
            If colorMode <> 3 AndAlso colorMode <> 1 AndAlso colorMode <> 4 Then Return Nothing
            If depth = 32 AndAlso colorMode = 4 Then Return Nothing
            Dim bytesPerSample = depth \ 8
            Dim grayscale = colorMode = 1
            Dim cmyk = colorMode = 4

            If Not SkipBlock(fs, ReadU32(fs)) Then Return Nothing  ' Farbmodus-Daten

            ' BILDRESSOURCEN: hier steht das ICC-Profil des Dokuments (Ressource 1039). Frueher
            ' wurde die Sektion nur uebersprungen, und die Ebenen kamen ohne Farbmanagement heraus.
            ' Gelesen wird mit derselben Routine wie im flachen Weg - zwei Leser fuer denselben
            ' Block liefen irgendwann auseinander. Beim reinen Verzeichnislesen bleibt es beim
            ' Ueberspringen: dort gibt es keine Bildpunkte, auf die ein Profil passen wuerde.
            Dim resourceLen = CLng(ReadU32(fs))
            ' NUR DIE BYTES, noch kein Profil. Zwischen dieser Stelle und dem fertigen Dokument
            ' liegen Abbrueche UND Ausnahmen (ein abgeschnittener Kopf laesst ReadU32 werfen). Ein
            ' fertiges Profil waere ab hier natives Eigentum, das durch jeden dieser Pfade getragen
            ' werden muesste; ein Bytefeld raeumt die Laufzeitumgebung selbst ab. Erzeugt wird es
            ' deshalb erst unten, wenn das Dokument steht.
            Dim iccBytes As Byte() = Nothing
            ' Der eigene Rezeptblock wird in derselben Sektion gesucht. Beim reinen Verzeichnislesen
            ' nur, OB er da ist - dafür liest niemand bis zu 128 MB.
            Dim resourceStart = fs.Position
            Dim resourceEnd = resourceStart + resourceLen
            If resourceEnd > fs.Length Then Return Nothing
            Dim hasRecipe = False
            Dim recipePayload = PsdRecipeService.FindPayload(fs, resourceEnd, Not metadataOnly, hasRecipe)
            fs.Position = resourceStart
            If metadataOnly Then
                If Not SkipBlock(fs, resourceLen) Then Return Nothing
            Else
                iccBytes = PsdPreviewService.ReadIccBytesFromResources(fs, resourceLen)
            End If
            ' Der Leser steht danach hinter der Sektion; ein Abbruch mittendrin (unbekannter Block)
            ' darf die folgenden Angaben nicht verschieben.
            If fs.Position <> resourceEnd Then fs.Position = resourceEnd

            Dim sectionLen = If(isPsb, ReadU64(fs), CLng(ReadU32(fs)))
            If sectionLen <= 0 Then Return Nothing
            Dim sectionEnd = fs.Position + sectionLen
            If sectionEnd > fs.Length Then Return Nothing

            ' Bei 16 und 32 Bit legt Photoshop die Ebenen nicht in die Ebenen-Info, sondern in einen
            ' eigenen Block dahinter (Lr16, Lr32); SeekLayerCount findet beide Stellen.
            If Not SeekLayerCount(fs, isPsb, sectionEnd) Then Return Nothing

            ' Negativ heißt: der erste Alphakanal des Gesamtbilds ist die Transparenz. Für die
            ' Ebenen selbst ändert das nichts, nur das Vorzeichen der Anzahl.
            Dim rawCount = ReadU16(fs)
            If rawCount > 32767 Then rawCount -= 65536
            Dim layerCount = Math.Abs(rawCount)
            If layerCount = 0 OrElse layerCount > MaxLayers Then Return Nothing

            Dim records As New List(Of LayerRecord)()
            Dim totalPixels As Long = 0
            Dim maxTemporaryPixels As Long = 0
            For i = 0 To layerCount - 1
                Dim rec = ReadLayerRecord(fs, isPsb)
                If rec Is Nothing Then Return Nothing
                totalPixels += CLng(Math.Max(0, rec.Right - rec.Left)) * Math.Max(0, rec.Bottom - rec.Top)
                ' Die Maske zählt mit. Sie ist ein weiteres Raster im Speicher, und eine Datei, in
                ' der jede Ebene eine bildgroße Maske trägt, käme sonst auf das Doppelte des Deckels.
                If rec.HasMaskRect Then
                    totalPixels += CLng(Math.Max(0, rec.MaskRight - rec.MaskLeft)) *
                                   Math.Max(0, rec.MaskBottom - rec.MaskTop)
                End If
                ' Beide Masken und ihr umschließendes Ergebnis liegen gleichzeitig im Speicher.
                If rec.CombineVectorMask Then
                    Dim maskPad = CLng(Math.Ceiling(rec.PixelFeather * 4.0))
                    Dim vectorPad = CLng(Math.Ceiling(rec.VectorFeather * 4.0))
                    Dim unionWidth = Math.Max(CLng(rec.MaskRight) + maskPad, CLng(rec.VectorMaskRight) + vectorPad) -
                                     Math.Min(CLng(rec.MaskLeft) - maskPad, CLng(rec.VectorMaskLeft) - vectorPad)
                    Dim unionHeight = Math.Max(CLng(rec.MaskBottom) + maskPad, CLng(rec.VectorMaskBottom) + vectorPad) -
                                      Math.Min(CLng(rec.MaskTop) - maskPad, CLng(rec.VectorMaskTop) - vectorPad)
                    If unionWidth <= 0 OrElse unionHeight <= 0 OrElse unionWidth > MaxTotalLayerPixels \ unionHeight Then Return Nothing
                    Dim temporaryPixels = CLng(Math.Max(0, rec.VectorMaskRight - rec.VectorMaskLeft)) *
                                          Math.Max(0, rec.VectorMaskBottom - rec.VectorMaskTop) + unionWidth * unionHeight
                    totalPixels += unionWidth * unionHeight
                    ' Beim Weichzeichnen bestehen gepolsterte Ein- und Ausgabebitmaps sowie das
                    ' vergrößerte Ergebnis neben den ursprünglichen Masken. Konservativ beide
                    ' Masken gleichzeitig zählen, bevor eines dieser Raster angelegt wird.
                    For Each mask In {New Long() {CLng(rec.MaskRight) - rec.MaskLeft, CLng(rec.MaskBottom) - rec.MaskTop, maskPad},
                                      New Long() {CLng(rec.VectorMaskRight) - rec.VectorMaskLeft, CLng(rec.VectorMaskBottom) - rec.VectorMaskTop, vectorPad}}
                        If mask(2) > 0 Then
                            Dim paddedWidth = mask(0) + 4L * mask(2)
                            Dim paddedHeight = mask(1) + 4L * mask(2)
                            If paddedWidth <= 0 OrElse paddedHeight <= 0 OrElse paddedWidth > MaxTotalLayerPixels \ paddedHeight Then Return Nothing
                            temporaryPixels += 3L * paddedWidth * paddedHeight
                        End If
                    Next
                    maxTemporaryPixels = Math.Max(maxTemporaryPixels, temporaryPixels)
                End If
                If totalPixels + maxTemporaryPixels > MaxTotalLayerPixels Then Return Nothing
                records.Add(rec)
            Next

            ' CMYK bleibt profilfrei, genau wie im flachen Weg: die Wandlung nach RGB ist dort
            ' eine grobe Naeherung, und ein RGB-Profil passt nicht auf vier Kanaele.
            Dim iccProfile As SKColorSpace = Nothing
            If iccBytes IsNot Nothing AndAlso Not cmyk Then
                Try
                    iccProfile = SKColorSpace.CreateIcc(iccBytes)
                Catch
                    ' Ein Block, der sich Profil nennt, aber keines ist, darf die Datei nicht
                    ' unlesbar machen - die Ebenen sind davon unberuehrt und gelten dann als sRGB.
                    iccProfile = Nothing
                End Try
            End If
            Dim doc As New PsdDocumentInfo With {.Width = docWidth, .Height = docHeight, .IccProfile = iccProfile,
                                                 .HasRecipeBlock = hasRecipe, .RecipePayload = recipePayload,
                                                 .LayerRecordCount = layerCount}
            For Each rec In records
                If rec.IsAdjustment AndAlso (rec.Flags And 2) = 0 Then doc.AdjustmentLayers += 1
            Next
            Dim layers = doc.Layers
            ' Bricht das Lesen mittendrin ab - eine defekte Ebene oder eine Ausnahme -, sind die
            ' Ebenen davor bereits dekodiert und haengen als Bitmap in der Liste, die danach
            ' niemand mehr in die Hand bekommt. Bei vierhundert Megapixeln Deckel ist das
            ' entsprechend viel nativer Speicher bis zum Finalisierer.
            Dim completed = False
            Try
                For Each rec In records
                    Dim maskBmp As SKBitmap = Nothing
                    Dim bmp = ReadChannelData(fs, rec, metadataOnly, maskBmp, bytesPerSample, grayscale, cmyk, isPsb)
                    ' Eine Ebene, deren Bildpunkte sich nicht lesen lassen, fehlt im Stapel und
                    ' wird gezählt; die Datei geht deshalb nicht mehr als Ganzes flach auf. Der
                    ' Lesezeiger steht nach ReadChannelData in jedem Fall hinter ihren Kanälen.
                    If bmp Is Nothing AndAlso rec.HasPixels AndAlso Not metadataOnly Then
                        doc.BrokenLayers += 1
                        Continue For
                    End If
                    ' Gruppenmarken haben keine Bildpunkte und gehen trotzdem mit: ohne sie wüsste
                    ' der Import nicht, wo eine Gruppe anfängt und wo sie aufhört. Ebenso eine
                    ' Textebene ohne sichtbaren Punkt (Text in durchsichtiger Farbe): sie hat ein
                    ' leeres Rechteck, ihr Wortlaut steht aber im Block TySh und soll als Text
                    ' zurückkommen können. Pixels bleibt dann Nothing. Und eine gewöhnliche Bildebene
                    ' ohne Inhalt: sie kommt als leere Malebene zurück (IsEmptyLayer).
                    Dim emptyText = Not rec.HasPixels AndAlso Not String.IsNullOrWhiteSpace(rec.TextContent)
                    Dim emptyLayer = Not rec.HasPixels AndAlso rec.IsPlainEmpty
                    If bmp Is Nothing AndAlso rec.SectionType = 0 AndAlso Not metadataOnly AndAlso Not emptyText AndAlso Not emptyLayer Then Continue For
                    If metadataOnly AndAlso Not rec.HasPixels AndAlso rec.SectionType = 0 AndAlso Not emptyText AndAlso Not emptyLayer Then Continue For

                    layers.Add(New PsdLayerInfo With {
                        .Name = rec.Name,
                        .Left = rec.Left,
                        .Top = rec.Top,
                        .Width = rec.Right - rec.Left,
                        .Height = rec.Bottom - rec.Top,
                        .OpacityPercent = CSng(rec.Opacity) * CSng(rec.FillOpacity) * 100.0F / (255.0F * 255.0F),
                        .BlendMode = ResolveBlendName(rec.BlendKey),
                        .ClipToLayerBelow = rec.Clipping <> 0,
                        .IsVisible = (rec.Flags And 2) = 0,
                        .Pixels = bmp,
                        .IsEmptyLayer = emptyLayer,
                        .TextContent = rec.TextContent,
                        .TextStyle = rec.TextStyle,
                        .MaskPixels = maskBmp,
                        .MaskLeft = rec.MaskLeft,
                        .MaskTop = rec.MaskTop,
                        .MaskWidth = If(maskBmp IsNot Nothing, rec.MaskRight - rec.MaskLeft, 0),
                        .MaskHeight = If(maskBmp IsNot Nothing, rec.MaskBottom - rec.MaskTop, 0),
                        .MaskDefaultValue = rec.MaskDefaultValue,
                        .MaskDisabled = rec.MaskDisabled,
                        .MaskFeatherPixels = CSng(2.0 * rec.AppliedFeather),
                        .SectionType = rec.SectionType,
                        .HasEffects = rec.HasEffects,
                        .LockPosition = (rec.ProtectionFlags And &H80000004L) <> 0,
                        .LockTransparency = (rec.ProtectionFlags And &H80000001L) <> 0 OrElse (rec.Flags And 1) <> 0
                    })
                Next
                completed = True
                Return doc
            Finally
                If Not completed Then
                    DisposeLayerPixels(doc)
                    ' Das Dokument geht nicht hinaus, also gehoert auch sein Profil hier weg.
                    doc.IccProfile?.Dispose()
                    doc.IccProfile = Nothing
                End If
            End Try
        End Function

        ''' <summary>Zusatzblöcke, deren Länge in einer PSB acht statt vier Byte lang ist (laut Adobes
        ''' Formatbeschreibung). Wer sie mit vier Byte liest, verliert jeden Block dahinter - in einem
        ''' Ebenendatensatz auch die Gruppenmarke, und die Gruppen zerfallen.</summary>
        Private Shared ReadOnly PsbLongLengthKeys As New HashSet(Of String)(StringComparer.Ordinal) From {
            "LMsk", "Lr16", "Lr32", "Layr", "Mt16", "Mt32", "Mtrn", "Alph", "FMsk", "lnk2", "FEid", "FXid", "PxSD"}

        ''' Auch für das Gesamtbild, das die globalen Zusatzblöcke nach Transparenzmarken absucht.
        Friend Shared Function IsPsbLongLengthKey(key As String) As Boolean
            Return PsbLongLengthKeys.Contains(key)
        End Function

        Private Shared Function ReadBlockLength(fs As FileStream, isPsb As Boolean, key As String) As Long
            Return If(isPsb AndAlso PsbLongLengthKeys.Contains(key), ReadU64(fs), CLng(ReadU32(fs)))
        End Function

        ''' <summary>Setzt den Lesezeiger auf die Ebenenzahl. Der Zeiger steht beim Aufruf am Anfang
        ''' der Ebenen-Info, also hinter der Länge des ganzen Abschnitts.
        '''
        ''' Normalfall: die Ebenen-Info trägt die Ebenen. Bei 16 und 32 Bit ist sie dagegen leer
        ''' (Länge 0 oder Ebenenzahl 0), und die Ebenen stehen in einem Zusatzblock HINTER der
        ''' globalen Maske, `Lr16` bzw. `Lr32`, mit genau dem Aufbau der Ebenen-Info. Vorher brach
        ''' der Leser an der leeren Ebenen-Info ab, und jede echte 16-Bit-Datei aus Photoshop ging
        ''' flach auf; die handgebauten Prüfdateien legten ihre Ebenen am Standardplatz ab und
        ''' sahen das nicht.
        '''
        ''' Ob der Block mit der Ebenenzahl beginnt oder noch eine eigene Länge vorneweg trägt, ist
        ''' in den Beschreibungen nicht einheitlich. Steht vorne genau die Restlänge des Blocks,
        ''' wird sie übersprungen.</summary>
        Private Shared Function SeekLayerCount(fs As FileStream, isPsb As Boolean, sectionEnd As Long) As Boolean
            Dim infoLen = If(isPsb, ReadU64(fs), CLng(ReadU32(fs)))
            If infoLen < 0 Then Return False
            Dim infoStart = fs.Position
            If infoStart + infoLen > sectionEnd Then Return False
            If infoLen >= 2 Then
                Dim count = ReadU16(fs)
                If count <> 0 Then
                    fs.Position = infoStart
                    Return True
                End If
            End If

            ' Hinter der Ebenen-Info: die globale Maske (Länge immer vier Byte), dann die Zusatzblöcke.
            fs.Position = infoStart + infoLen
            If fs.Position + 4 > sectionEnd Then Return False
            If Not SkipBlock(fs, ReadU32(fs)) Then Return False
            While fs.Position + 12 <= sectionEnd
                Dim sig(3) As Byte
                If Not ReadExactly(fs, sig, 4) Then Return False
                Dim s = Encoding.ASCII.GetString(sig)
                If s <> "8BIM" AndAlso s <> "8B64" Then Return False
                Dim keyBytes(3) As Byte
                If Not ReadExactly(fs, keyBytes, 4) Then Return False
                Dim key = Encoding.ASCII.GetString(keyBytes)
                Dim length = ReadBlockLength(fs, isPsb, key)
                Dim dataStart = fs.Position
                If length < 0 OrElse dataStart + length > sectionEnd Then Return False
                If key = "Lr16" OrElse key = "Lr32" Then
                    If length >= 6 Then
                        Dim lead = If(isPsb, ReadU64(fs), CLng(ReadU32(fs)))
                        Dim leadBytes = If(isPsb, 8L, 4L)
                        ' Eine vorangestellte Länge zählt den Rest des Blocks, höchstens um die
                        ' Auffüllung auf vier Byte kürzer.
                        If lead > 0 AndAlso lead <= length - leadBytes AndAlso lead >= length - leadBytes - 3 Then
                            Return True
                        End If
                    End If
                    fs.Position = dataStart
                    Return True
                End If
                ' Auf vier Byte ausgerichtet; Photoshop zählt die Auffüllung meist schon mit, dann
                ' ändert das Ausrichten nichts.
                fs.Position = dataStart + ((length + 3L) And Not 3L)
            End While
            Return False
        End Function

        ''' <summary>Gibt die Bildpunkte aller schon gelesenen Ebenen frei. Fuer die Abbruchwege -
        ''' wer Nothing zurueckbekommt, hat keine Handhabe mehr auf das, was bis dahin entstand.</summary>
        Private Shared Sub DisposeLayerPixels(doc As PsdDocumentInfo)
            If doc Is Nothing Then Return
            For Each layer In doc.Layers
                layer.Pixels?.Dispose()
                layer.Pixels = Nothing
                layer.MaskPixels?.Dispose()
                layer.MaskPixels = Nothing
            Next
            doc.Layers.Clear()
        End Sub

        ' ── Ebenenverzeichnis ────────────────────────────────────────────────────

        Private Class LayerRecord
            Public Property Top As Integer
            Public Property Left As Integer
            Public Property Bottom As Integer
            Public Property Right As Integer
            Public Property Name As String = ""
            Public Property BlendKey As String = "norm"
            Public Property Opacity As Integer = 255
            ''' Füllkraft aus dem Block iOpa, 0 bis 255.
            Public Property FillOpacity As Integer = 255
            Public Property Clipping As Integer
            Public Property Flags As Integer
            ''' Die Sperren aus dem Block lspf.
            Public Property ProtectionFlags As Long
            ''' Kanalkennung und Länge, in genau der Reihenfolge, in der die Daten später folgen.
            Public Property Channels As New List(Of Integer())()
            ''' Eine Gruppenmarke oder eine Ebene ohne Fläche trägt keine Bildpunkte.
            Public Property HasPixels As Boolean = True
            Public Property TextContent As String = ""
            Public Property TextStyle As PsdEngineData
            ''' Das Rechteck der Ebenenmaske, in Dokumentkoordinaten wie das der Ebene.
            Public Property MaskTop As Integer
            Public Property MaskLeft As Integer
            Public Property MaskBottom As Integer
            Public Property MaskRight As Integer
            Public Property MaskDefaultValue As Byte = 0
            Public Property MaskDisabled As Boolean
            Public Property HasMaskRect As Boolean = False
            ''' <summary>Der Kanal, aus dem die Maske kommt: -2 gewöhnlich, -3, wenn die Ebene
            ''' neben der Vektormaske eine gemalte trägt (siehe ReadMaskBlock).</summary>
            Public Property MaskChannelId As Integer = -2
            ''' Merkmal Bit 3: die Maske in Kanal -2 hat Photoshop aus Vektordaten gerechnet.
            Public Property MaskFromVectorData As Boolean
            Public Property PixelDensity As Byte = 255
            Public Property VectorDensity As Byte = 255
            Public Property PixelFeather As Double
            Public Property VectorFeather As Double
            Public Property AppliedFeather As Double
            ''' Die zweite Maske des Maskenblocks, zu Kanal -3: Rechteck, Vorgabewert, Merkmale.
            Public Property HasRealMask As Boolean
            Public Property RealMaskTop As Integer
            Public Property RealMaskLeft As Integer
            Public Property RealMaskBottom As Integer
            Public Property RealMaskRight As Integer
            Public Property RealMaskDefaultValue As Byte
            Public Property RealMaskDisabled As Boolean
            ''' <summary>Kanal -2 wird zusätzlich gelesen und mit -3 multipliziert: eine
            ''' gewöhnliche Ebene mit gemalter Maske UND Vektormaske. Dafür sein Rechteck und
            ''' Vorgabewert, solange Mask* schon auf -3 steht.</summary>
            Public Property CombineVectorMask As Boolean
            Public Property VectorMaskTop As Integer
            Public Property VectorMaskLeft As Integer
            Public Property VectorMaskBottom As Integer
            Public Property VectorMaskRight As Integer
            Public Property VectorMaskDefaultValue As Byte
            Public Property SectionType As Integer = 0
            Public Property HasEffects As Boolean
            Public Property IsAdjustment As Boolean
            ''' <summary>Photoshop hat die Bildpunkte dieser Ebene aus etwas anderem gerechnet:
            ''' Text, Form oder Smartobjekt. Ihre Kante steckt dann schon in den Bildpunkten.</summary>
            Public Property IsRendered As Boolean
            ''' <summary>Eine gewöhnliche Bildebene, nur ohne Inhalt. Zählt erst, wenn
            ''' <see cref="HasPixels"/> falsch ist.</summary>
            Public Property IsPlainEmpty As Boolean
        End Class

        Private Shared Function ReadLayerRecord(fs As FileStream, isPsb As Boolean) As LayerRecord
            Dim rec As New LayerRecord With {
                .Top = CInt(ReadU32Signed(fs)),
                .Left = CInt(ReadU32Signed(fs)),
                .Bottom = CInt(ReadU32Signed(fs)),
                .Right = CInt(ReadU32Signed(fs))
            }

            Dim channelCount = ReadU16(fs)
            If channelCount < 1 OrElse channelCount > 56 Then Return Nothing
            For i = 0 To channelCount - 1
                Dim id = ReadU16(fs)
                If id > 32767 Then id -= 65536
                Dim len = If(isPsb, ReadU64(fs), CLng(ReadU32(fs)))
                ' Die nachfolgende Dekodierung und Positionsrechnung arbeiten mit Integer-Laengen.
                ' Ein PSB-Kanal ueber 2 GB darf deshalb nicht still geklemmt werden: dann zeigte
                ' blockEnd in den falschen Datenblock und alle folgenden Ebenen wurden fehlgelesen.
                If len < 0 OrElse len > Integer.MaxValue Then Return Nothing
                rec.Channels.Add(New Integer() {id, CInt(len)})
            Next

            Dim sig(3) As Byte
            If Not ReadExactly(fs, sig, 4) Then Return Nothing
            If Encoding.ASCII.GetString(sig) <> "8BIM" Then Return Nothing
            Dim key(3) As Byte
            If Not ReadExactly(fs, key, 4) Then Return Nothing
            rec.BlendKey = Encoding.ASCII.GetString(key)

            rec.Opacity = fs.ReadByte()
            rec.Clipping = fs.ReadByte()
            rec.Flags = fs.ReadByte()
            If fs.ReadByte() < 0 Then Return Nothing  ' Füllbyte

            Dim extraLen = ReadU32(fs)
            Dim extraEnd = fs.Position + extraLen
            If extraEnd > fs.Length Then Return Nothing

            Dim maskLen = ReadU32(fs)
            If maskLen < 0 OrElse fs.Position + maskLen > fs.Length Then Return Nothing
            If maskLen > 0 Then
                Dim maskEnd = fs.Position + maskLen
                ReadMaskBlock(fs, rec, maskLen)
                ' In JEDEM Fall auf das Blockende springen: ReadMaskBlock liest nur, was es braucht,
                ' und verlassen wird der Block über seine Länge.
                fs.Seek(maskEnd, SeekOrigin.Begin)
            End If
            Dim rangesLen = ReadU32(fs)
            If Not SkipBlock(fs, rangesLen) Then Return Nothing

            ' Alter Name: Längenbyte, Text, das ganze Feld auf ein Vielfaches von vier aufgefüllt.
            Dim nameLen = fs.ReadByte()
            If nameLen < 0 Then Return Nothing
            Dim nameBytes(Math.Max(0, nameLen - 1)) As Byte
            If nameLen > 0 AndAlso Not ReadExactly(fs, nameBytes, nameLen) Then Return Nothing
            rec.Name = If(nameLen > 0, Encoding.GetEncoding(28591).GetString(nameBytes, 0, nameLen), "")
            Dim consumed = 1 + nameLen
            While consumed Mod 4 <> 0
                If fs.ReadByte() < 0 Then Return Nothing
                consumed += 1
            End While

            ' Zusatzblöcke: der Unicode-Name, der Text, die Füllkraft, die Gruppenmarke und die
            ' Effekte. Von den übrigen zählt nur, DASS sie da sind: an ihnen ist zu erkennen, ob
            ' Photoshop die Bildpunkte aus Text, Form oder Smartobjekt gerechnet hat.
            Dim hasText = False, hasSmart = False, hasShapeContent = False
            Dim hasVectorMask = False, hasFill = False, hasStroke = False
            Dim effectsBlock As Byte() = Nothing
            Dim effectsKey = ""
            Dim hasLegacyEffects = False
            While fs.Position + 12 <= extraEnd
                Dim blockSig(3) As Byte
                If Not ReadExactly(fs, blockSig, 4) Then Exit While
                Dim s = Encoding.ASCII.GetString(blockSig)
                If s <> "8BIM" AndAlso s <> "8B64" Then Exit While
                Dim blockKey(3) As Byte
                If Not ReadExactly(fs, blockKey, 4) Then Exit While
                Dim bk = Encoding.ASCII.GetString(blockKey)
                If AdjustmentKeys.Contains(bk) Then rec.IsAdjustment = True
                Dim blockLen = ReadBlockLength(fs, isPsb, bk)
                Dim blockStart = fs.Position
                If blockStart + blockLen > extraEnd Then Exit While

                If bk = "luni" AndAlso blockLen >= 4 Then
                    Dim charCount = ReadU32(fs)
                    If charCount >= 0 AndAlso charCount < 4096 AndAlso blockStart + 4 + charCount * 2 <= extraEnd Then
                        Dim sb As New StringBuilder()
                        For i = 1L To charCount
                            Dim hi = fs.ReadByte()
                            Dim lo = fs.ReadByte()
                            If hi < 0 OrElse lo < 0 Then Exit For
                            sb.Append(ChrW(hi * 256 + lo))
                        Next
                        If sb.Length > 0 Then rec.Name = sb.ToString()
                    End If
                ElseIf bk = "TySh" AndAlso blockLen > 100 AndAlso blockLen < 8_000_000 Then
                    ' Textebene: den Block als Ganzes einlesen und den Wortlaut daraus holen.
                    hasText = True
                    Dim textBlock(CInt(blockLen) - 1) As Byte
                    If ReadExactly(fs, textBlock, textBlock.Length) Then
                        rec.TextContent = PsdTextReader.ExtractText(textBlock)
                        rec.TextStyle = PsdTextReader.ExtractStyle(textBlock)
                    End If
                ElseIf (bk = "lmfx" OrElse bk = "lfx2" OrElse bk = "lfxs") AndAlso blockLen >= 8 AndAlso blockLen < 8_000_000 Then
                    ' Effekte. "lmfx" schreibt Photoshop, sobald eine Effektart mehrfach vorkommt,
                    ' und es geht dann "lfx2" vor; bei Gruppen heißt der Block "lfxs".
                    If effectsBlock Is Nothing OrElse bk = "lmfx" Then
                        Dim fx(CInt(blockLen) - 1) As Byte
                        If ReadExactly(fs, fx, fx.Length) Then
                            effectsBlock = fx
                            effectsKey = bk
                        End If
                    End If
                ElseIf bk = "lrFX" Then
                    ' Die alte Form der Effekte. Neuere Fassungen schreiben "lfx2" daneben, das dann
                    ' gilt; an Gruppen übergeht Photoshop "lrFX" ganz.
                    hasLegacyEffects = True
                ElseIf bk = "SoLd" OrElse bk = "SoLE" OrElse bk = "PlLd" OrElse bk = "plLd" Then
                    hasSmart = True
                ElseIf bk = "vscg" OrElse bk = "vogk" Then
                    hasShapeContent = True
                ElseIf bk = "vmsk" OrElse bk = "vsms" Then
                    hasVectorMask = True
                ElseIf bk = "SoCo" OrElse bk = "GdFl" OrElse bk = "PtFl" Then
                    hasFill = True
                ElseIf bk = "vstk" Then
                    hasStroke = True
                ElseIf bk = "lspf" AndAlso blockLen >= 4 Then
                    ' Sperren: Bit 0 Transparenz, Bit 1 Bildpunkte, Bit 2 Position, Bit 31 alles.
                    rec.ProtectionFlags = ReadU32(fs)
                ElseIf bk = "iOpa" AndAlso blockLen >= 1 Then
                    ' Füllkraft: wirkt auf den Inhalt der Ebene, nicht auf ihre Effekte. Ohne Effekte
                    ' ist sie dasselbe wie eine Deckkraft. Übergangen erschien eine Ebene mit
                    ' Füllkraft 0, die nur über eine Kontur sichtbar sein soll, voll deckend.
                    Dim fill = fs.ReadByte()
                    If fill >= 0 Then rec.FillOpacity = fill
                ElseIf (bk = "lsct" OrElse bk = "lsdk") AndAlso blockLen >= 4 Then
                    ' Abschnittsmarke: 1 und 2 sind die Gruppenzeile selbst, 3 ist ihr unteres Ende.
                    ' Solche Datensätze tragen keine Bildpunkte, werden aber weitergereicht - aus
                    ' ihnen baut der Import die Gruppen nach. "lsdk" ist derselbe Block unter
                    ' anderem Namen, wie ihn manche Fassungen bei verschachtelten Gruppen schreiben;
                    ' übergangen zerfielen diese Gruppen in lose Ebenen.
                    Dim sectionType = ReadU32(fs)
                    If sectionType >= 1 AndAlso sectionType <= 3 Then
                        rec.HasPixels = False
                        rec.SectionType = CInt(sectionType)
                    End If
                End If

                fs.Seek(blockStart + blockLen + (blockLen And 1L), SeekOrigin.Begin)
            End While

            fs.Seek(extraEnd, SeekOrigin.Begin)

            If rec.Right <= rec.Left OrElse rec.Bottom <= rec.Top Then rec.HasPixels = False

            ' Eine Form ist ein Pfad mit Füllung oder Kontur; eine Füllebene mit Vektormaske ist in
            ' Photoshop dasselbe.
            rec.IsRendered = hasText OrElse hasSmart OrElse hasShapeContent OrElse
                             (hasVectorMask AndAlso (hasFill OrElse hasStroke))
            ' Eine gewöhnliche Bildebene ohne einen Punkt: kein Text, keine Form, kein Smartobjekt,
            ' keine Füll- oder Einstellungsebene. Sie geht als leere Ebene mit, statt zu fehlen -
            ' der Export schreibt solche Ebenen ausdrücklich, und ohne sie verlöre ein Rundweg
            ' Namen, Platz im Stapel und Maske.
            rec.IsPlainEmpty = rec.SectionType = 0 AndAlso Not rec.IsAdjustment AndAlso
                               Not (hasText OrElse hasSmart OrElse hasShapeContent OrElse hasFill OrElse hasStroke)
            If effectsBlock IsNot Nothing Then
                rec.HasEffects = EffectsAreActive(effectsBlock)
            ElseIf hasLegacyEffects AndAlso rec.SectionType = 0 Then
                rec.HasEffects = True
            End If
            SelectMaskChannel(rec)
            Return rec
        End Function

        Private Shared ReadOnly AdjustmentKeys As New HashSet(Of String)(StringComparer.Ordinal) From {
            "curv", "levl", "expA", "brit", "CgEd", "vibA", "hue2", "hue ", "nvrt",
            "thrs", "mixr", "clrL", "post", "grdm", "blnc", "selc", "phfl", "blwh"}

        ''' <summary>Entscheidet, welche Maske die Ebene bekommt.
        '''
        ''' Trägt eine Ebene neben einer gemalten Maske auch eine Vektormaske, legt Photoshop die
        ''' gemalte in Kanal -3 ab, mit dem zweiten Rechteck des Maskenblocks, und in Kanal -2 die
        ''' aus dem Pfad gerechnete. Photoshop multipliziert beide. Bei einer gewöhnlichen Ebene
        ''' werden deshalb beide gelesen und multipliziert (CombineMaskPlanes); nur -3 genommen,
        ''' tauchte alles wieder auf, was die Vektormaske abschneidet. Bei Text, Form und
        ''' Smartobjekt kommt nur -3: die Vektorkante steckt dort schon in den Bildpunkten.
        ''' Ist eine der beiden abgeschaltet, gilt die andere allein.
        '''
        ''' Ohne -3 kommt -2. Hat Photoshop sie aus Vektordaten gerechnet (Merkmal Bit 3) und ist die
        ''' Ebene Text, Form oder Smartobjekt, entfällt sie: deren Bildpunkte tragen diese Kante
        ''' schon, und noch einmal maskiert würde jede Kante dünner. Bei einer gewöhnlichen Ebene
        ''' bleibt sie, denn dort ist sie die einzige Spur der Vektormaske.</summary>
        Private Shared Sub SelectMaskChannel(rec As LayerRecord)
            Dim hasRealChannel = False, hasVectorChannel = False
            For Each ch In rec.Channels
                If ch(0) = -3 Then hasRealChannel = True
                If ch(0) = -2 Then hasVectorChannel = True
            Next

            ' Die gemalte ist abgeschaltet: bei einer gewöhnlichen Ebene wirkt dann nur die
            ' Vektormaske, und die steht in -2 wie ohne gemalte.
            If rec.HasRealMask AndAlso hasRealChannel AndAlso rec.RealMaskDisabled AndAlso
               Not rec.IsRendered AndAlso rec.HasMaskRect AndAlso hasVectorChannel Then
                rec.MaskChannelId = -2
                Return
            End If

            If rec.HasRealMask AndAlso hasRealChannel Then
                rec.CombineVectorMask = Not rec.IsRendered AndAlso hasVectorChannel AndAlso
                                        rec.HasMaskRect AndAlso Not rec.MaskDisabled
                If rec.CombineVectorMask Then
                    rec.VectorMaskTop = rec.MaskTop
                    rec.VectorMaskLeft = rec.MaskLeft
                    rec.VectorMaskBottom = rec.MaskBottom
                    rec.VectorMaskRight = rec.MaskRight
                    rec.VectorMaskDefaultValue = rec.MaskDefaultValue
                End If
                rec.MaskChannelId = -3
                rec.MaskTop = rec.RealMaskTop
                rec.MaskLeft = rec.RealMaskLeft
                rec.MaskBottom = rec.RealMaskBottom
                rec.MaskRight = rec.RealMaskRight
                rec.MaskDefaultValue = rec.RealMaskDefaultValue
                rec.MaskDisabled = rec.RealMaskDisabled
                rec.HasMaskRect = True
                Return
            End If

            rec.MaskChannelId = -2
            If rec.MaskFromVectorData AndAlso rec.IsRendered Then rec.HasMaskRect = False
        End Sub

        ''' <summary>Ob ein Effektblock ("lfx2", "lmfx", "lfxs") wirklich etwas zeigt: der
        ''' Hauptschalter an und mindestens ein Effekt eingeschaltet. Photoshop behält den Block,
        ''' wenn der Anwender die Effekte nur ausblendet.
        '''
        ''' Aufbau: vier Byte Version der Effekte, dann eine Beschreibungsstruktur mit eigener
        ''' Version. Darin "masterFXSwitch" und je Effektart ein Eintrag mit "enab" (und bei
        ''' manchen "present"), bei mehrfachen Effekten eine Liste solcher Einträge.
        '''
        ''' Lässt sich die Struktur nicht lesen, gilt der Block als aktiv: lieber einmal zu viel
        ''' gemeldet als eine Ebene, deren Kontur fehlt, ohne Hinweis.</summary>
        Friend Shared Function EffectsAreActive(block As Byte()) As Boolean
            Dim root = PsdDescriptor.ParseVersioned(block, 4)
            If root Is Nothing Then Return True
            If IsFalse(root.GetValue("masterFXSwitch")) Then Return False

            For Each item In root.Items
                Dim effect = TryCast(item.Value, PsdDescriptor)
                If effect IsNot Nothing AndAlso IsEffectEnabled(effect) Then Return True
                Dim list = TryCast(item.Value, List(Of Object))
                If list Is Nothing Then Continue For
                For Each element In list
                    effect = TryCast(element, PsdDescriptor)
                    If effect IsNot Nothing AndAlso IsEffectEnabled(effect) Then Return True
                Next
            Next
            Return False
        End Function

        ''' Fehlt "enab", gilt der Effekt als eingeschaltet, wie bei Photoshop selbst.
        Private Shared Function IsEffectEnabled(effect As PsdDescriptor) As Boolean
            Return Not IsFalse(effect.GetValue("enab")) AndAlso Not IsFalse(effect.GetValue("present"))
        End Function

        Private Shared Function IsFalse(value As Object) As Boolean
            Return TypeOf value Is Boolean AndAlso Not DirectCast(value, Boolean)
        End Function

        ''' <summary>Der Maskenteil eines Ebenendatensatzes: Rechteck, Vorgabewert außerhalb davon,
        ''' Merkmale. Der Aufrufer springt danach über die Blocklänge weiter, hier wird also nur
        ''' gelesen, was gebraucht wird.
        '''
        ''' Das Merkmal-Byte trägt in Bit 1 das Abschalten der Maske und in Bit 0 die Aussage, dass
        ''' ihr Rechteck RELATIV zur Ebene gemeint ist statt absolut im Dokument. Das kommt selten
        ''' vor, kostet aber nichts - und übergangen säße die Maske um den Ebenenversatz verschoben,
        ''' was bei einer Ebene am Bildrand sofort auffiele und bei einer bildgroßen nie.
        '''
        ''' Bit 3 heißt: Photoshop hat die Maske aus Vektordaten gerechnet. Bit 4 heißt: es folgen
        ''' Parameter (Dichte, weiche Kante). Trägt die Ebene eine gemalte Maske UND eine
        ''' Vektormaske, steht dahinter ein zweiter Satz für Kanal -3: Merkmale, Vorgabewert,
        ''' Rechteck (siehe <see cref="RealMaskComesFirst"/> zur Reihenfolge).</summary>
        Private Shared Sub ReadMaskBlock(fs As FileStream, rec As LayerRecord, maskLen As Long)
            ' Rechteck, Vorgabewert und Merkmale sind zusammen achtzehn Byte. Weniger heißt: hier
            ' steht etwas anderes, als diese Fassung des Formats vorsieht.
            If maskLen < 18 Then Return
            Try
                ' Der Block ist klein (höchstens rund sechzig Byte); gelesen wird er am Stück. Wo
                ' er endet, bestimmt der Aufrufer über die Länge.
                Dim data(CInt(Math.Min(maskLen, 256L)) - 1) As Byte
                If Not ReadExactly(fs, data, data.Length) Then Return
                Dim length = CInt(Math.Min(maskLen, Integer.MaxValue))

                Dim flags = CInt(data(17))
                rec.MaskFromVectorData = (flags And 8) <> 0
                If ApplyMaskRect(rec, data, 0, flags,
                                 Sub(top, left, bottom, right)
                                     rec.MaskTop = top
                                     rec.MaskLeft = left
                                     rec.MaskBottom = bottom
                                     rec.MaskRight = right
                                 End Sub) Then
                    ' Der Wert wird UNVERAENDERT uebernommen. Das Format nennt zwar nur 0 und 255,
                    ' aber es steht ein ganzes Byte dort, und ein Grauwert dazwischen ist eine
                    ' Maske, die ausserhalb ihres Rechtecks halb deckt. Auf die beiden Enden
                    ' gerundet aendert sich die Deckkraft der Ebene sichtbar, und zwar ueberall
                    ' dort, wo die Maske nichts sagt.
                    rec.MaskDefaultValue = data(16)
                    rec.MaskDisabled = (flags And 2) <> 0
                    rec.HasMaskRect = True
                End If

                ' Der zweite Satz: Merkmale, Vorgabewert, dann das Rechteck.
                Dim realAt = -1
                If (flags And 16) = 0 Then
                    If length >= 36 Then realAt = 18
                ElseIf RealMaskComesFirst(data, length) Then
                    realAt = 18
                Else
                    Dim afterParameters = 19 + MaskParameterBytes(data(18))
                    If length >= afterParameters + 18 Then realAt = afterParameters
                End If
                If (flags And 16) <> 0 Then
                    Dim parameterAt = If(realAt = 18, 36, 18)
                    If parameterAt < data.Length Then
                        Dim pf = CInt(data(parameterAt))
                        Dim pos = parameterAt + 1
                        If pos + MaskParameterBytes(CByte(pf)) <= data.Length Then
                            If (pf And 1) <> 0 Then
                                rec.PixelDensity = data(pos)
                                pos += 1
                            End If
                            If (pf And 2) <> 0 Then
                                rec.PixelFeather = ReadMaskFeather(data, pos)
                                pos += 8
                            End If
                            If (pf And 4) <> 0 Then
                                rec.VectorDensity = data(pos)
                                pos += 1
                            End If
                            If (pf And 8) <> 0 Then rec.VectorFeather = ReadMaskFeather(data, pos)
                        End If
                    End If
                End If
                If realAt >= 0 AndAlso realAt + 18 <= data.Length Then
                    Dim realFlags = CInt(data(realAt))
                    If ApplyMaskRect(rec, data, realAt + 2, realFlags,
                                     Sub(top, left, bottom, right)
                                         rec.RealMaskTop = top
                                         rec.RealMaskLeft = left
                                         rec.RealMaskBottom = bottom
                                         rec.RealMaskRight = right
                                     End Sub) Then
                        rec.RealMaskDefaultValue = data(realAt + 1)
                        rec.RealMaskDisabled = (realFlags And 2) <> 0
                        rec.HasRealMask = True
                    End If
                End If
            Catch
                ' Ein unlesbarer Maskenblock kostet die Maske, nicht die Ebene.
                rec.HasMaskRect = False
                rec.HasRealMask = False
            End Try
        End Sub

        ''' <summary>Liest ein Maskenrechteck (oben, links, unten, rechts) ab
        ''' <paramref name="offset"/> und gibt es weiter, wenn es eine Fläche hat. Bit 0 der
        ''' Merkmale verschiebt es um die Lage der Ebene (siehe ReadMaskBlock).</summary>
        Private Shared Function ApplyMaskRect(rec As LayerRecord, data As Byte(), offset As Integer,
                                              flags As Integer,
                                              apply As Action(Of Integer, Integer, Integer, Integer)) As Boolean
            Dim top = ReadI32(data, offset)
            Dim left = ReadI32(data, offset + 4)
            Dim bottom = ReadI32(data, offset + 8)
            Dim right = ReadI32(data, offset + 12)
            If (flags And 1) <> 0 Then
                left += rec.Left : right += rec.Left
                top += rec.Top : bottom += rec.Top
            End If
            If right <= left OrElse bottom <= top Then Return False
            apply(top, left, bottom, right)
            Return True
        End Function

        ''' Wie viele Byte die Parameter hinter ihrem Merkmalbyte belegen: Bit 0 Dichte der
        ''' gemalten Maske (1 Byte), Bit 1 ihre weiche Kante (8 Byte), Bit 2 und 3 dasselbe für die
        ''' Vektormaske.
        Private Shared Function MaskParameterBytes(parameterFlags As Byte) As Integer
            Return If((parameterFlags And 1) <> 0, 1, 0) + If((parameterFlags And 2) <> 0, 8, 0) +
                   If((parameterFlags And 4) <> 0, 1, 0) + If((parameterFlags And 8) <> 0, 8, 0)
        End Function

        Private Shared Function ReadMaskFeather(data As Byte(), offset As Integer) As Double
            Dim bytes(7) As Byte
            Buffer.BlockCopy(data, offset, bytes, 0, 8)
            If BitConverter.IsLittleEndian Then Array.Reverse(bytes)
            Dim value = BitConverter.ToDouble(bytes, 0)
            If Double.IsNaN(value) OrElse Double.IsInfinity(value) OrElse value < 0 OrElse value > 10000 Then Return 0
            Return value
        End Function

        ''' <summary>Steht der zweite Maskensatz VOR den Parametern? Die Formatbeschreibung nennt
        ''' die Parameter zuerst, Photoshop schreibt den Satz zuerst. Entschieden wird an der Länge
        ''' und an der Plausibilität, abgeleitet aus Dateien, die Photoshop geschrieben hat:
        ''' passen die Parameter ab Byte 18 allein mit höchstens drei Byte Auffüllung in den Block,
        ''' gibt es gar keinen zweiten Satz. Passen sie erst ab Byte 36, gilt Photoshops
        ''' Reihenfolge, es sei denn, ihre Lesart ist unplausibel (Vorgabewert weder 0 noch 255,
        ''' unbekannte Merkmalbits) und die der Beschreibung nicht.</summary>
        Private Shared Function RealMaskComesFirst(data As Byte(), length As Integer) As Boolean
            Dim fits = Function(at As Integer) As Boolean
                           If at >= data.Length Then Return False
                           Dim size = MaskParameterBytes(data(at))
                           Return length >= at + 1 + size AndAlso length <= at + 4 + size
                       End Function
            If fits(18) Then Return False
            If Not fits(36) Then Return False

            Dim knownFlags = Function(pf As Byte) (pf And &HF0) = 0
            Dim backgroundOk = Function(at As Integer) at + 1 < data.Length AndAlso
                                                       (data(at + 1) = 0 OrElse data(at + 1) = 255)
            Dim photoshopPlausible = backgroundOk(18) AndAlso knownFlags(data(36))
            Dim realAfter = 19 + MaskParameterBytes(data(18))
            Dim specPlausible = knownFlags(data(18)) AndAlso
                                length >= realAfter + 18 AndAlso length <= realAfter + 21 AndAlso
                                backgroundOk(realAfter)
            Return photoshopPlausible OrElse Not specPlausible
        End Function

        Private Shared Function ReadI32(data As Byte(), offset As Integer) As Integer
            Dim value = (CLng(data(offset)) << 24) Or (CLng(data(offset + 1)) << 16) Or
                        (CLng(data(offset + 2)) << 8) Or CLng(data(offset + 3))
            Return CInt(If(value > Integer.MaxValue, value - 4_294_967_296L, value))
        End Function

        ' ── Kanaldaten ───────────────────────────────────────────────────────────

        ''' <summary>Liest die Kanäle einer Ebene und setzt sie zu einem Bitmap zusammen. Kanäle, die
        ''' nicht zum Bild gehören, werden mitgelesen und verworfen, sonst verrutscht der Lesezeiger
        ''' für alle folgenden Ebenen.</summary>
        ''' <param name="maskBitmap">Nimmt die Ebenenmaske auf, wenn eine dabei ist. Sie steht in
        ''' einem eigenen Kanal mit eigenem Rechteck und kann deshalb nicht aus dem Rückgabewert
        ''' kommen.</param>
        ''' <param name="grayscale">Das Dokument hat nur einen Farbkanal. Er wird auf alle drei
        ''' gelegt - Grau ist Rot gleich Grün gleich Blau, und der Ebenenstapel kennt nur Farbe.</param>
        ''' <param name="cmyk">Die vier Farbkanäle sind Cyan, Magenta, Gelb und Schwarz. Photoshop
        ''' speichert sie invertiert; die profilfreie Umrechnung entspricht dem flachen PSD-Weg.</param>
        Private Shared Function ReadChannelData(fs As FileStream, rec As LayerRecord,
                                                metadataOnly As Boolean,
                                                ByRef maskBitmap As SKBitmap,
                                                bytesPerSample As Integer,
                                                grayscale As Boolean,
                                                cmyk As Boolean,
                                                isPsb As Boolean) As SKBitmap
            Dim width = rec.Right - rec.Left
            Dim height = rec.Bottom - rec.Top
            Dim maskWidth = rec.MaskRight - rec.MaskLeft
            Dim maskHeight = rec.MaskBottom - rec.MaskTop
            Dim vectorWidth = rec.VectorMaskRight - rec.VectorMaskLeft
            Dim vectorHeight = rec.VectorMaskBottom - rec.VectorMaskTop
            Dim maskPlane As Byte() = Nothing, vectorPlane As Byte() = Nothing
            maskBitmap = Nothing

            Dim red As Byte() = Nothing, green As Byte() = Nothing, blue As Byte() = Nothing, alpha As Byte() = Nothing
            Dim black As Byte() = Nothing

            For Each ch In rec.Channels
                Dim id = ch(0)
                Dim declaredLen = ch(1)
                Dim blockEnd = fs.Position + declaredLen

                ' Nur die Bildkanäle einer Ebene mit Fläche werden ausgewertet: RGB/Grau braucht
                ' drei beziehungsweise einen, CMYK vier; Transparenz liegt bei allen in Kanal -1.
                Dim wanted = Not metadataOnly AndAlso rec.HasPixels AndAlso
                             (id = 0 OrElse id = 1 OrElse id = 2 OrElse id = -1 OrElse (cmyk AndAlso id = 3))
                ' Die Maske liegt in Kanal -2 oder -3 (welcher, entscheidet SelectMaskChannel) und
                ' hat die Maße IHRES Rechtecks, nicht die der Ebene.
                ' Auch eine GRUPPENZEILE darf eine Maske tragen; sie gilt dann für alles, was in der
                ' Gruppe liegt. Deshalb hängt das Lesen hier nicht an den Bildpunkten. Dasselbe gilt
                ' für eine leere Textebene, die als Text zurückkommen soll, und eine leere Bildebene.
                Dim carriesMask = rec.HasPixels OrElse rec.SectionType <> 0 OrElse rec.IsPlainEmpty OrElse
                                  Not String.IsNullOrWhiteSpace(rec.TextContent)
                Dim wantMask = Not metadataOnly AndAlso carriesMask AndAlso
                               rec.HasMaskRect AndAlso id = rec.MaskChannelId AndAlso maskWidth > 0 AndAlso maskHeight > 0
                Dim wantVectorMask = Not metadataOnly AndAlso carriesMask AndAlso
                                     rec.CombineVectorMask AndAlso id = -2 AndAlso vectorWidth > 0 AndAlso vectorHeight > 0
                If wanted Then
                    Dim plane = ReadPlane(fs, width, height, declaredLen, bytesPerSample, isPsb, colorChannel:=id >= 0)
                    If plane IsNot Nothing Then
                        Select Case id
                            Case 0 : red = plane
                            Case 1 : green = plane
                            Case 2 : blue = plane
                            Case 3 : black = plane
                            Case -1 : alpha = plane
                        End Select
                    End If
                ElseIf wantMask Then
                    ' Scheitert die Maske, bleibt die Ebene gültig - sie sieht dann aus wie ohne
                    ' Maske, und das ist der Stand von vorher, kein neuer Schaden.
                    maskPlane = ReadPlane(fs, maskWidth, maskHeight, declaredLen, bytesPerSample, isPsb, colorChannel:=False)
                ElseIf wantVectorMask Then
                    vectorPlane = ReadPlane(fs, vectorWidth, vectorHeight, declaredLen, bytesPerSample, isPsb, colorChannel:=False)
                End If

                ' In jedem Fall exakt hinter den Block springen: die angegebene Länge ist die
                ' Wahrheit, nicht das, was das Entpacken verbraucht hat.
                If blockEnd > fs.Length Then Return DropMask(maskBitmap)
                fs.Seek(blockEnd, SeekOrigin.Begin)
            Next

            ' Die Maske erst nach allen Kanälen bauen: bei zwei Masken stehen -2 und -3 in
            ' beliebiger Reihenfolge, und multipliziert wird erst, wenn beide da sind.
            If maskPlane IsNot Nothing Then
                rec.AppliedFeather = If(rec.MaskChannelId = -3 OrElse Not rec.MaskFromVectorData, rec.PixelFeather, rec.VectorFeather)
                Dim density = If(rec.MaskChannelId = -3 OrElse Not rec.MaskFromVectorData, rec.PixelDensity, rec.VectorDensity)
                ApplyMaskDensity(maskPlane, density)
                rec.MaskDefaultValue = MaskDensityValue(rec.MaskDefaultValue, density)
            End If
            If vectorPlane IsNot Nothing Then
                ApplyMaskDensity(vectorPlane, rec.VectorDensity)
                rec.VectorMaskDefaultValue = MaskDensityValue(rec.VectorMaskDefaultValue, rec.VectorDensity)
            End If
            If maskPlane IsNot Nothing AndAlso vectorPlane IsNot Nothing Then
                maskPlane = FeatherMaskPlane(maskPlane, rec.MaskLeft, rec.MaskTop, rec.MaskRight, rec.MaskBottom,
                                             rec.MaskDefaultValue, rec.PixelFeather)
                vectorPlane = FeatherMaskPlane(vectorPlane, rec.VectorMaskLeft, rec.VectorMaskTop,
                                               rec.VectorMaskRight, rec.VectorMaskBottom,
                                               rec.VectorMaskDefaultValue, rec.VectorFeather)
                rec.AppliedFeather = 0
                maskBitmap = CombineMaskPlanes(rec, maskPlane, vectorPlane)
            ElseIf maskPlane IsNot Nothing Then
                maskBitmap = BuildAlphaBitmap(maskPlane, maskWidth, maskHeight)
            ElseIf vectorPlane IsNot Nothing Then
                ' Die gemalte ließ sich nicht lesen: dann wenigstens die Vektormaske allein.
                rec.MaskTop = rec.VectorMaskTop
                rec.MaskLeft = rec.VectorMaskLeft
                rec.MaskBottom = rec.VectorMaskBottom
                rec.MaskRight = rec.VectorMaskRight
                rec.MaskDefaultValue = rec.VectorMaskDefaultValue
                rec.MaskDisabled = False
                rec.AppliedFeather = rec.VectorFeather
                maskBitmap = BuildAlphaBitmap(vectorPlane, vectorWidth, vectorHeight)
            End If

            ' Kommt keine Ebene zustande, ist auch ihre Maske gegenstandslos - und ein Bitmap, das
            ' niemand mehr in die Hand bekommt, wäre nativer Speicher bis zum Finalisierer. Bei
            ' einer Gruppenzeile ist das anders: sie hat nie Bildpunkte, ihre Maske gilt trotzdem.
            If Not rec.HasPixels Then
                ' Gruppenzeile, leere Textebene und leere Bildebene behalten ihre Maske (carriesMask oben).
                If rec.SectionType <> 0 OrElse rec.IsPlainEmpty OrElse Not String.IsNullOrWhiteSpace(rec.TextContent) Then Return Nothing
                Return DropMask(maskBitmap)
            End If
            ' Bei Graustufen gibt es nur den einen Kanal 0. Er wird auf alle drei gelegt, statt einen
            ' zweiten Zusammenbauweg daneben zu stellen - die Werte sind dieselben.
            If grayscale AndAlso red IsNot Nothing Then
                green = red
                blue = red
            End If
            If red Is Nothing OrElse green Is Nothing OrElse blue Is Nothing OrElse (cmyk AndAlso black Is Nothing) Then Return DropMask(maskBitmap)

            Dim info = New SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul)
            Dim bmp = New SKBitmap(info)
            Try
                Dim count = width * height
                Dim buffer(count * 4 - 1) As Byte
                For i = 0 To count - 1
                    If cmyk Then
                        ' Photoshop speichert CMYK invertiert (255 = keine Farbe). Ohne das
                        ' eingebettete Druckprofil ist dies bewusst dieselbe robuste Näherung wie
                        ' PsdPreviewService: C', M', Y' jeweils mit K' multiplizieren.
                        '
                        ' Die REIHENFOLGE ist hier eine andere als dort: diese Bitmap ist Rgba8888,
                        ' die der Vorschau Bgra8888. Wer die Rechnung von dort übernimmt, ohne die
                        ' beiden Kanäle zu drehen, bekommt Rot und Blau vertauscht - und sieht es
                        ' nur an einer CMYK-Datei mit Ebenen, weil das flache Gesamtbild denselben
                        ' Weg gar nicht geht.
                        Dim k = CInt(black(i))
                        buffer(i * 4) = CByte(CInt(red(i)) * k \ 255)       ' R aus C'
                        buffer(i * 4 + 1) = CByte(CInt(green(i)) * k \ 255) ' G aus M'
                        buffer(i * 4 + 2) = CByte(CInt(blue(i)) * k \ 255)  ' B aus Y'
                    Else
                        buffer(i * 4) = red(i)
                        buffer(i * 4 + 1) = green(i)
                        buffer(i * 4 + 2) = blue(i)
                    End If
                    buffer(i * 4 + 3) = If(alpha IsNot Nothing, alpha(i), CByte(255))
                Next
                Runtime.InteropServices.Marshal.Copy(buffer, 0, bmp.GetPixels(), buffer.Length)
                Return bmp
            Catch
                bmp.Dispose()
                Return DropMask(maskBitmap)
            End Try
        End Function

        ''' <summary>Gibt eine schon gelesene Maske wieder frei und liefert Nothing. Für die
        ''' Abbruchwege des Kanallesers, damit dort nicht an drei Stellen dasselbe steht.</summary>
        Private Shared Function DropMask(ByRef maskBitmap As SKBitmap) As SKBitmap
            maskBitmap?.Dispose()
            maskBitmap = Nothing
            Return Nothing
        End Function

        ''' <summary>Multipliziert die gemalte Maske (Kanal -3, Rechteck in Mask*) mit der
        ''' Vektormaske (Kanal -2, Rechteck in VectorMask*), wie Photoshop beide zusammen wirken
        ''' lässt. Jede trägt außerhalb ihres Rechtecks ihren Vorgabewert; das Ergebnis umfasst
        ''' beide Rechtecke, und außerhalb gilt das Produkt der Vorgabewerte. Rechteck und
        ''' Vorgabewert der Ebene werden auf das Ergebnis umgestellt.</summary>
        Private Shared Function CombineMaskPlanes(rec As LayerRecord, realPlane As Byte(), vectorPlane As Byte()) As SKBitmap
            Dim realLeft = rec.MaskLeft, realTop = rec.MaskTop
            Dim realWidth = rec.MaskRight - rec.MaskLeft, realHeight = rec.MaskBottom - rec.MaskTop
            Dim vecLeft = rec.VectorMaskLeft, vecTop = rec.VectorMaskTop
            Dim vecWidth = rec.VectorMaskRight - rec.VectorMaskLeft, vecHeight = rec.VectorMaskBottom - rec.VectorMaskTop
            Dim left = Math.Min(rec.MaskLeft, rec.VectorMaskLeft)
            Dim top = Math.Min(rec.MaskTop, rec.VectorMaskTop)
            Dim right = Math.Max(rec.MaskRight, rec.VectorMaskRight)
            Dim bottom = Math.Max(rec.MaskBottom, rec.VectorMaskBottom)
            Dim wideWidth = CLng(right) - left, wideHeight = CLng(bottom) - top
            If wideWidth <= 0 OrElse wideHeight <= 0 OrElse wideWidth > MaxTotalLayerPixels \ wideHeight Then
                Throw New InvalidOperationException("PSD combined mask exceeds memory limit")
            End If
            Dim width = CInt(wideWidth), height = CInt(wideHeight)

            Dim realDefault = CInt(rec.MaskDefaultValue)
            Dim vecDefault = CInt(rec.VectorMaskDefaultValue)
            Dim combined(width * height - 1) As Byte
            For y = 0 To height - 1
                Dim docY = top + y
                Dim realRow = docY - realTop, vecRow = docY - vecTop
                For x = 0 To width - 1
                    Dim docX = left + x
                    Dim rx = docX - realLeft, vx = docX - vecLeft
                    Dim r = If(rx >= 0 AndAlso rx < realWidth AndAlso realRow >= 0 AndAlso realRow < realHeight,
                               CInt(realPlane(realRow * realWidth + rx)), realDefault)
                    Dim v = If(vx >= 0 AndAlso vx < vecWidth AndAlso vecRow >= 0 AndAlso vecRow < vecHeight,
                               CInt(vectorPlane(vecRow * vecWidth + vx)), vecDefault)
                    combined(y * width + x) = CByte((r * v + 127) \ 255)
                Next
            Next

            rec.MaskLeft = left
            rec.MaskTop = top
            rec.MaskRight = right
            rec.MaskBottom = bottom
            rec.MaskDefaultValue = CByte((realDefault * vecDefault + 127) \ 255)
            Return BuildAlphaBitmap(combined, width, height)
        End Function

        Private Shared Function MaskDensityValue(value As Byte, density As Byte) As Byte
            Return CByte(255 - ((255 - CInt(value)) * CInt(density) + 127) \ 255)
        End Function

        Private Shared Sub ApplyMaskDensity(plane As Byte(), density As Byte)
            If density = 255 Then Return
            For i = 0 To plane.Length - 1
                plane(i) = MaskDensityValue(plane(i), density)
            Next
        End Sub

        ''' Beide Masken müssen vor ihrer Multiplikation einzeln weichgezeichnet werden. Der Rand
        ''' wird mit dem PSD-Vorgabewert aufgefüllt; transparentes Padding wäre bei Vorgabe Weiß falsch.
        Private Shared Function FeatherMaskPlane(plane As Byte(), ByRef left As Integer, ByRef top As Integer,
                                                 ByRef right As Integer, ByRef bottom As Integer,
                                                 defaultValue As Byte, sigma As Double) As Byte()
            If sigma <= 0.025 Then Return plane
            Dim width = right - left, height = bottom - top
            Dim innerPad = CInt(Math.Ceiling(sigma * 4.0))
            Dim pad = innerPad * 2
            Dim paddedWidth = CLng(width) + 2L * pad, paddedHeight = CLng(height) + 2L * pad
            If paddedWidth * paddedHeight > MaxTotalLayerPixels OrElse paddedWidth > Integer.MaxValue OrElse
               paddedHeight > Integer.MaxValue Then Throw New InvalidOperationException("PSD mask feather exceeds memory limit")
            Dim w = CInt(paddedWidth), h = CInt(paddedHeight)
            Using input = BuildAlphaBitmap(plane, width, height),
                  padded = New SKBitmap(w, h, SKColorType.Alpha8, SKAlphaType.Premul),
                  result = New SKBitmap(w, h, SKColorType.Alpha8, SKAlphaType.Premul)
                If input Is Nothing Then Throw New InvalidOperationException("Invalid PSD mask")
                Using canvas = New SKCanvas(padded)
                    canvas.Clear(New SKColor(0, 0, 0, defaultValue))
                    Using paint = New SKPaint With {.BlendMode = SKBlendMode.Src}
                        canvas.DrawBitmap(input, pad, pad, paint)
                    End Using
                End Using
                Using canvas = New SKCanvas(result), paint = New SKPaint()
                    paint.ImageFilter = SKImageFilter.CreateBlur(CSng(sigma), CSng(sigma))
                    canvas.DrawBitmap(padded, 0, 0, paint)
                End Using
                Dim outWidth = width + 2 * innerPad, outHeight = height + 2 * innerPad
                Dim output(outWidth * outHeight - 1) As Byte
                For y = 0 To outHeight - 1
                    Runtime.InteropServices.Marshal.Copy(IntPtr.Add(result.GetPixels(), (y + innerPad) * result.RowBytes + innerPad), output, y * outWidth, outWidth)
                Next
                left -= innerPad : top -= innerPad : right += innerPad : bottom += innerPad
                Return output
            End Using
        End Function

        ''' <summary>Eine Graustufenfläche als Alpha8-Bitmap. Genau die Bauform, die der eigene
        ''' Maskentyp erwartet - dort liegen die Deckungswerte im Alphakanal, nicht in Helligkeiten.</summary>
        Private Shared Function BuildAlphaBitmap(plane As Byte(), width As Integer, height As Integer) As SKBitmap
            If plane Is Nothing OrElse width < 1 OrElse height < 1 Then Return Nothing
            If plane.Length < width * height Then Return Nothing
            Dim bmp = New SKBitmap(New SKImageInfo(width, height, SKColorType.Alpha8, SKAlphaType.Premul))
            Try
                ' ZEILENWEISE über RowBytes. Skia darf die Zeilenlänge aufrunden, und ein Kopieren
                ' der Fläche am Stück verschöbe dann jede Zeile gegen die vorige.
                Dim target = bmp.GetPixels()
                Dim stride = bmp.RowBytes
                For y = 0 To height - 1
                    Runtime.InteropServices.Marshal.Copy(plane, y * width, IntPtr.Add(target, y * stride), width)
                Next
                Return bmp
            Catch
                bmp.Dispose()
                Return Nothing
            End Try
        End Function

        ''' <summary>Ein Kanal einer Ebene: Kompressionsmarke, bei RLE die Zeilenlängen dieses einen
        ''' Kanals, danach die Zeilen. Anders als beim Gesamtbild stehen die Längen NICHT gesammelt
        ''' für alle Kanäle vorneweg.</summary>
        ''' <param name="declaredLen">Die im Verzeichnis angegebene Länge dieses Kanalblocks,
        ''' EINSCHLIESSLICH der zwei Byte für die Kompressionsmarke. Nur der ZIP-Weg braucht sie: ein
        ''' Deflate-Strom sagt selbst nicht, wo er aufhört, und weiterzulesen als der Block reicht
        ''' hieße in den nächsten Kanal hinein.</param>
        ''' <param name="bytesPerSample">1 bei acht Bit, 2 bei sechzehn, 4 bei 32 Bit Gleitkomma.
        ''' Herauskommt immer ein Byte je Bildpunkt - bei sechzehn Bit das obere, wie es der flache
        ''' Weg auch tut, bei 32 Bit siehe <see cref="FloatSampleToByte"/>.</param>
        ''' <param name="isPsb">In einer PSB sind die RLE-Zeilenlängen vier Byte lang statt zwei.</param>
        ''' <param name="colorChannel">Ein Farbkanal (Kennung ab 0) und keine Deckung (Transparenz,
        ''' Maske). Zählt nur bei 32 Bit: dort ist Farbe linear abgelegt, Deckung nicht.</param>
        Private Shared Function ReadPlane(fs As FileStream, width As Integer, height As Integer,
                                          declaredLen As Integer, bytesPerSample As Integer,
                                          isPsb As Boolean, colorChannel As Boolean) As Byte()
            If width < 1 OrElse height < 1 Then Return Nothing
            Dim compression = ReadU16(fs)
            Dim plane(width * height - 1) As Byte

            ' Eine Maske kann in einem 32-Bit-Dokument schmaler abgelegt sein als die Farbe. Die
            ' Blocklänge sagt bei unkomprimierten Daten genau, wie breit ein Wert ist.
            If compression = 0 AndAlso bytesPerSample > 1 AndAlso
               CLng(declaredLen) - 2 = CLng(width) * height Then bytesPerSample = 1
            Dim rowBytes = width * bytesPerSample

            If compression = 0 Then
                Dim rowBuffer(rowBytes - 1) As Byte
                For row = 0 To height - 1
                    If Not ReadExactly(fs, rowBuffer, rowBytes) Then Return Nothing
                    StoreRow(plane, row, width, rowBuffer, bytesPerSample, colorChannel)
                Next
                Return plane
            End If

            ' 2 und 3 sind ZIP: derselbe Deflate-Strom, bei 3 zusätzlich zeilenweise als Differenz
            ' zum linken Nachbarn abgelegt. Photoshop schreibt bei acht Bit meist RLE, andere
            ' Programme und alles ab sechzehn Bit greifen zu ZIP - ohne diesen Zweig lieferte der
            ' Leser dort gar keine Ebenen, und die Datei fiel auf das flache Gesamtbild zurück.
            If compression = 2 OrElse compression = 3 Then
                Return ReadZipPlane(fs, width, height, declaredLen - 2, compression = 3, bytesPerSample, colorChannel)
            End If

            If compression <> 1 Then Return Nothing

            ' Die Zeilenlängen sind GEPACKTE Längen. Der Schlimmstfall von PackBits ist etwas mehr
            ' als die rohe Zeile, deshalb der großzügige Rand - alles darüber ist keine gültige Datei.
            ' In einer PSB sind sie vier Byte lang. Mit zwei gelesen verrutschte alles danach, und die
            ' Datei ging flach auf; das Gesamtbild machte es schon immer richtig.
            Dim rowLengths(height - 1) As Integer
            For row = 0 To height - 1
                Dim length = If(isPsb, ReadU32(fs), CLng(ReadU16(fs)))
                If length < 0 OrElse length > rowBytes * 2L + 64 Then Return Nothing
                rowLengths(row) = CInt(length)
            Next

            Dim packed(Math.Max(1, rowBytes * 2 + 64) - 1) As Byte
            Dim unpacked(rowBytes - 1) As Byte
            For row = 0 To height - 1
                Dim len = rowLengths(row)
                If packed.Length < len Then ReDim packed(len - 1)
                If Not ReadExactly(fs, packed, len) Then Return Nothing
                If Not PsdPreviewService.UnpackBits(packed, len, unpacked, rowBytes) Then Return Nothing
                StoreRow(plane, row, width, unpacked, bytesPerSample, colorChannel)
            Next
            Return plane
        End Function

        ''' <summary>Legt eine entpackte Zeile in die Kanalfläche. Bei sechzehn Bit wird dabei das
        ''' obere Byte genommen: der Ebenenstapel rechnet in acht Bit, und derselbe Griff steht seit
        ''' jeher im flachen Weg. Bei 32 Bit stehen Gleitkommazahlen, oberes Byte zuerst.</summary>
        Private Shared Sub StoreRow(plane As Byte(), row As Integer, width As Integer,
                                    rowBuffer As Byte(), bytesPerSample As Integer, colorChannel As Boolean)
            Dim target = row * width
            If bytesPerSample = 1 Then
                Array.Copy(rowBuffer, 0, plane, target, width)
                Return
            End If
            If bytesPerSample = 4 Then
                For x = 0 To width - 1
                    Dim o = x * 4
                    Dim bits = (CInt(rowBuffer(o)) << 24) Or (CInt(rowBuffer(o + 1)) << 16) Or
                               (CInt(rowBuffer(o + 2)) << 8) Or CInt(rowBuffer(o + 3))
                    plane(target + x) = FloatSampleToByte(BitConverter.Int32BitsToSingle(bits), colorChannel)
                Next
                Return
            End If
            For x = 0 To width - 1
                plane(target + x) = rowBuffer(x * 2)
            Next
        End Sub

        ''' <summary>Stufen der Tabelle von linear nach sRGB. 4096 genügen: der Abstand zweier Stufen
        ''' ist im Dunkeln, wo die Kurve am steilsten ist, kleiner als ein Ausgabewert.</summary>
        Private Const LinearTableSize As Integer = 4096

        Private Shared ReadOnly LinearToSrgbTable As Byte() = BuildLinearToSrgbTable()

        Private Shared Function BuildLinearToSrgbTable() As Byte()
            Dim table(LinearTableSize) As Byte
            For i = 0 To LinearTableSize
                Dim v = i / CDbl(LinearTableSize)
                Dim s = If(v <= 0.0031308, v * 12.92, 1.055 * Math.Pow(v, 1.0 / 2.4) - 0.055)
                table(i) = CByte(Math.Max(0.0, Math.Min(255.0, Math.Round(s * 255.0))))
            Next
            Return table
        End Function

        ''' <summary>Ein 32-Bit-Wert als Byte. Photoshop legt Farbe im 32-Bit-Modus LINEAR ab
        ''' (Gamma 1,0); sie wird nach sRGB umgerechnet, sonst käme jede Ebene viel zu dunkel. Was
        ''' über 1 hinausgeht, wird abgeschnitten - der Ebenenstapel kennt nur acht Bit. Transparenz
        ''' und Masken sind dagegen Deckung und gehen unverändert auf 0 bis 255.</summary>
        Private Shared Function FloatSampleToByte(value As Single, colorChannel As Boolean) As Byte
            If Single.IsNaN(value) OrElse value <= 0.0F Then Return 0
            If value >= 1.0F Then Return 255
            If Not colorChannel Then Return CByte(Math.Round(value * 255.0F))
            Return LinearToSrgbTable(CInt(Math.Round(value * LinearTableSize)))
        End Function

        ''' <summary>Die Vorhersage bei 32 Bit, wie beim Gleitkomma-Prädiktor von TIFF: die Zeile ist
        ''' in Byte-Ebenen zerlegt (erst alle oberen Bytes, dann die zweiten und so fort), und über
        ''' die ganze Zeile steht je Byte die Differenz zum vorigen. Zurück geht es in derselben
        ''' Reihenfolge: aufsummieren, dann die Ebenen wieder zu Werten verschränken.</summary>
        Private Shared Sub UndoFloatPrediction(raw As Byte(), offset As Integer, width As Integer)
            Dim rowBytes = width * 4
            For i = 1 To rowBytes - 1
                raw(offset + i) = CByte((CInt(raw(offset + i)) + CInt(raw(offset + i - 1))) And &HFF)
            Next
            Dim planar(rowBytes - 1) As Byte
            Array.Copy(raw, offset, planar, 0, rowBytes)
            For x = 0 To width - 1
                For b = 0 To 3
                    raw(offset + x * 4 + b) = planar(b * width + x)
                Next
            Next
        End Sub

        ''' <summary>Ein ZIP-komprimierter Kanal. <paramref name="payloadLen"/> ist die Länge des
        ''' Stroms ohne die Kompressionsmarke, <paramref name="predicted"/> unterscheidet die Marken
        ''' 2 und 3.
        '''
        ''' Bei der VORHERSAGE (Marke 3) steht in jedem Byte nicht der Wert, sondern die Differenz
        ''' zum linken Nachbarn; die Zeile beginnt jeweils neu. Die Rückrechnung läuft deshalb
        ''' ZEILENWEISE und nicht über die Fläche - sonst schleppte der erste Punkt einer Zeile den
        ''' letzten der vorigen mit sich, und das Bild zöge sich schräg auseinander.
        '''
        ''' Gerechnet wird über Integer und mit einer Maske zurück auf ein Byte. VB prüft
        ''' Ganzzahlüberläufe von sich aus, und eine Summe über 255 wäre sonst kein Umlauf, sondern
        ''' eine Ausnahme mitten im Entpacken.
        '''
        ''' Bei SECHZEHN Bit läuft die Vorhersage über ganze Werte und nicht über Bytes: die
        ''' Differenz steht dort als Wort, oberes Byte zuerst. Byteweise gerechnet käme aus jedem
        ''' Übertrag ein sichtbarer Fehler, der sich über die ganze Zeile fortpflanzt.</summary>
        Private Shared Function ReadZipPlane(fs As FileStream, width As Integer, height As Integer,
                                             payloadLen As Integer, predicted As Boolean,
                                             bytesPerSample As Integer, colorChannel As Boolean) As Byte()
            If payloadLen <= 0 Then Return Nothing
            If fs.Position + payloadLen > fs.Length Then Return Nothing

            Dim packed(payloadLen - 1) As Byte
            If Not ReadExactly(fs, packed, payloadLen) Then Return Nothing

            Dim rowBytes = width * bytesPerSample
            Dim raw(rowBytes * height - 1) As Byte
            If Not Inflate(packed, raw) Then
                ' Eine Maske in einem 32-Bit-Dokument kann schmaler abgelegt sein als die Farbe. Der
                ' breitere Versuch kommt zuerst: ein schmaler Strom füllt das breite Feld nicht.
                If bytesPerSample = 1 Then Return Nothing
                bytesPerSample = 1
                rowBytes = width
                raw = New Byte(rowBytes * height - 1) {}
                If Not Inflate(packed, raw) Then Return Nothing
            End If

            If predicted Then
                For row = 0 To height - 1
                    If bytesPerSample = 4 Then
                        UndoFloatPrediction(raw, row * rowBytes, width)
                    Else
                        PsdPreviewService.UndoPrediction(raw, row * rowBytes, width, bytesPerSample)
                    End If
                Next
            End If

            If bytesPerSample = 1 Then Return raw

            Dim plane(width * height - 1) As Byte
            Dim rowBuffer(rowBytes - 1) As Byte
            For row = 0 To height - 1
                Array.Copy(raw, row * rowBytes, rowBuffer, 0, rowBytes)
                StoreRow(plane, row, width, rowBuffer, bytesPerSample, colorChannel)
            Next
            Return plane
        End Function

        ''' <summary>Entpackt <paramref name="packed"/> nach <paramref name="target"/> und meldet, ob
        ''' die Zielfläche VOLLSTAENDIG gefüllt wurde. Ein halb entpackter Kanal ist kein brauchbares
        ''' Ergebnis: die untere Hälfte der Ebene bliebe schwarz, und das fiele erst im fertigen Bild
        ''' auf.
        '''
        ''' Erwartet wird ein zlib-Strom, so schreibt Photoshop ihn. Fehlen die zwei Kopfbytes -
        ''' andere Erzeuger legen gelegentlich den nackten Deflate-Strom ab -, wird der zweite Weg
        ''' versucht, statt die Ebene aufzugeben.</summary>
        Private Shared Function Inflate(packed As Byte(), target As Byte()) As Boolean
            Dim withHeader = packed IsNot Nothing AndAlso packed.Length >= 2 AndAlso
                             PsdPreviewService.LooksLikeZlib(packed(0), packed(1))
            If withHeader AndAlso InflateWith(packed, target, True) Then Return True
            Return InflateWith(packed, target, False)
        End Function

        Private Shared Function InflateWith(packed As Byte(), target As Byte(), zlibHeader As Boolean) As Boolean
            Try
                Using source = New MemoryStream(packed, False)
                    Using raw As Stream = If(zlibHeader,
                                             CType(New ZLibStream(source, CompressionMode.Decompress), Stream),
                                             CType(New DeflateStream(source, CompressionMode.Decompress), Stream))
                        Dim total = 0
                        While total < target.Length
                            Dim n = raw.Read(target, total, target.Length - total)
                            If n <= 0 Then Exit While
                            total += n
                        End While
                        Return total = target.Length
                    End Using
                End Using
            Catch
                Return False
            End Try
        End Function

        Private Shared Function ResolveBlendName(key As String) As String
            Dim name As String = Nothing
            If key IsNot Nothing AndAlso BlendNames.TryGetValue(key, name) Then Return name
            Return "Normal"
        End Function

        ' ── Lese-Helfer ──────────────────────────────────────────────────────────

        Private Shared Function ReadExactly(fs As FileStream, buffer As Byte(), count As Integer,
                                            Optional offset As Integer = 0) As Boolean
            Dim total = 0
            While total < count
                Dim n = fs.Read(buffer, offset + total, count - total)
                If n <= 0 Then Return False
                total += n
            End While
            Return True
        End Function

        Private Shared Function SkipBlock(fs As FileStream, length As Long) As Boolean
            If length < 0 OrElse fs.Position + length > fs.Length Then Return False
            fs.Seek(length, SeekOrigin.Current)
            Return True
        End Function

        Private Shared Function ReadU16(fs As FileStream) As Integer
            Dim b(1) As Byte
            If Not ReadExactly(fs, b, 2) Then Throw New EndOfStreamException()
            Return CInt(b(0)) * 256 + b(1)
        End Function

        ' Vor dem Schieben weiten, sonst bliebe der Ausdruck in VB ein Byte und ergäbe still 0.
        Private Shared Function ReadU32(fs As FileStream) As Long
            Dim b(3) As Byte
            If Not ReadExactly(fs, b, 4) Then Throw New EndOfStreamException()
            Return (CLng(b(0)) << 24) Or (CLng(b(1)) << 16) Or (CLng(b(2)) << 8) Or CLng(b(3))
        End Function

        ''' Die Ebenenrechtecke dürfen negativ sein: eine Ebene kann über den Bildrand hinausragen.
        Private Shared Function ReadU32Signed(fs As FileStream) As Long
            Dim v = ReadU32(fs)
            If v > 2147483647L Then v -= 4294967296L
            Return v
        End Function

        Private Shared Function ReadU64(fs As FileStream) As Long
            Dim b(7) As Byte
            If Not ReadExactly(fs, b, 8) Then Throw New EndOfStreamException()
            Return (CLng(b(0)) << 56) Or (CLng(b(1)) << 48) Or (CLng(b(2)) << 40) Or (CLng(b(3)) << 32) Or
                   (CLng(b(4)) << 24) Or (CLng(b(5)) << 16) Or (CLng(b(6)) << 8) Or CLng(b(7))
        End Function

    End Class

End Namespace
