Imports System
Imports System.Collections.Generic
Imports System.Runtime.InteropServices
Imports SkiaSharp
Imports HB = HarfBuzzSharp

Namespace Services

    ''' <summary>Eine geformte Textzeile: welche Glyphe wo steht, relativ zum Zeilenanfang auf der
    ''' Grundlinie, einschliesslich Zeichenabstand.</summary>
    Friend NotInheritable Class ShapedText
        Friend Glyphs As UShort() = Array.Empty(Of UShort)()
        ''' <summary>Lage der Glyphe (Stift plus Versatz aus der Formung).</summary>
        Friend X As Single() = Array.Empty(Of Single)()
        Friend Y As Single() = Array.Empty(Of Single)()
        ''' <summary>Stiftlage VOR der Glyphe, ohne ihren Versatz. Daran haengen Zeichengruppen
        ''' auf einem Pfad.</summary>
        Friend PenX As Single() = Array.Empty(Of Single)()
        Friend Advance As Single() = Array.Empty(Of Single)()
        ''' <summary>Laufende Nummer der Zeichengruppe je Glyphe, in Zeichenreihenfolge.</summary>
        Friend ClusterIndex As Integer() = Array.Empty(Of Integer)()
        Friend ClusterCount As Integer
        ''' <summary>Vorschub der ganzen Zeile, Zeichenabstand eingerechnet.</summary>
        Friend Width As Single
    End Class

    ''' <summary>TEXTFORMUNG FUER ALLES, WAS SKIA INS BILD SCHREIBT.
    '''
    ''' <c>SKCanvas.DrawText</c> mit einer Zeichenkette reiht die Glyphen nur mit ihren nackten
    ''' Vorschubbreiten auf. Fuer lateinische Schrift fehlt dadurch nur das Kerning; bei Thai,
    ''' Devanagari oder Arabisch fehlt die Anordnung selbst. Thai-Vokale und Tonzeichen ueber einem
    ''' Buchstaben landen dann auf derselben Hoehe und verdecken sich, aus "ที่" wird sichtbar "ที"
    ''' (Nutzerbefund aus Thailand: "die Vokale schweben"). Die Textbox des Editors zeigte es
    ''' richtig, weil Avalonia selbst ueber HarfBuzz formt - der Fehler stand also erst im Ergebnis.
    '''
    ''' Geformt wird mit HarfBuzzSharp, das Avalonia ohnehin mitbringt; ein eigenes Paket ist dafuer
    ''' nicht noetig. Gezeichnet wird Glyphe fuer Glyphe ueber die IntPtr-Aufrufe von SkiaSharp: die
    ''' Aufrufe, die ganze Positionslisten nehmen, erwarten Span-Parameter, und die kann VB nicht
    ''' uebergeben.
    '''
    ''' Mit Zeichenabstand sind Ligaturen abgeschaltet, wie im Web: eine Ligatur ist eine Gruppe, in
    ''' die kein Abstand faellt, "fi" stuende sonst enger als "fa". Kerning bleibt an.</summary>
    Friend Module TextShaper

        ''' <summary>HarfBuzz rechnet in ganzen Zahlen. Mit dieser Teilung liegt die Aufloesung bei
        ''' einem 65536stel der Schriftgroesse - weit unter einem Bildpunkt.</summary>
        Private Const ShapingScale As Integer = 65536

        ''' <summary>Je Schriftschnitt einmal aufgebaut. Eine HarfBuzz-Schrift ist nach dem Aufbau
        ''' unveraenderlich und darf aus mehreren Faeden zugleich formen; nur das Anlegen steht unter
        ''' dem Schloss.
        '''
        ''' Der Schluessel ist der Schnitt, nicht das verwaltete Objekt: SkiaSharp gibt fuer dieselbe
        ''' Schrift mitunter verschiedene Huellen heraus (etwa ueber SKFont.Typeface), und jede haette
        ''' sonst die ganze Schriftdatei ein weiteres Mal geladen. Die Eigenschaften werden aus der
        ''' nativen Schrift gelesen.</summary>
        Private ReadOnly _fonts As New Dictionary(Of String, HB.Font)(StringComparer.Ordinal)
        Private ReadOnly _fontsLock As New Object()

        Private Function GetTypefaceKey(typeface As SKTypeface) As String
            Return String.Join("|", typeface.FamilyName, typeface.PostScriptName, typeface.FontWeight, typeface.FontWidth,
                               CInt(typeface.FontSlant), typeface.GlyphCount, typeface.UnitsPerEm, typeface.TableCount)
        End Function

        Private ReadOnly _withoutLigatures As HB.Feature() = {
            HB.Feature.Parse("liga=0"),
            HB.Feature.Parse("clig=0")
        }

        Private Function GetShapingFont(typeface As SKTypeface) As HB.Font
            If typeface Is Nothing Then Return Nothing
            Dim key = GetTypefaceKey(typeface)
            SyncLock _fontsLock
                Dim cached As HB.Font = Nothing
                If _fonts.TryGetValue(key, cached) Then Return cached
                Dim created = CreateShapingFont(typeface)
                If created IsNot Nothing Then _fonts(key) = created
                Return created
            End SyncLock
        End Function

        Private Function CreateShapingFont(typeface As SKTypeface) As HB.Font
            Try
                Dim faceIndex As Integer
                Using asset = typeface.OpenStream(faceIndex)
                    If asset Is Nothing OrElse asset.Length <= 0 Then Return Nothing
                    Dim bytes(asset.Length - 1) As Byte
                    Dim read = asset.Read(bytes, bytes.Length)
                    If read <= 0 Then Return Nothing
                    ' NICHT Blob.FromStream: das pinnt sein Bytefeld nur waehrend des Konstruktors und
                    ' meldet es HarfBuzz als nur lesbar, also ohne Kopie. HarfBuzz liest danach aus
                    ' einem verwalteten Feld, das die Speicherbereinigung jederzeit verschieben darf -
                    ' im Pruefstand formte es nach einigen Dutzend Pruefungen mit Datenmuell ("H" und
                    ' "i" gleich breit). Die Schriftdaten liegen deshalb in nicht verwaltetem Speicher,
                    ' den erst HarfBuzz beim Freigeben des Blobs zurueckgibt.
                    Dim data = Marshal.AllocHGlobal(read)
                    Marshal.Copy(bytes, 0, data, read)
                    Dim blob As HB.Blob
                    Try
                        blob = New HB.Blob(data, read, HB.MemoryMode.ReadOnly, Sub() Marshal.FreeHGlobal(data))
                    Catch
                        Marshal.FreeHGlobal(data)
                        Throw
                    End Try
                    ' Font haelt Face und Face haelt Blob ueber HarfBuzz' eigene Zaehlung - die
                    ' verwalteten Huellen duerfen deshalb gleich wieder gehen.
                    Using blob
                        Using face = New HB.Face(blob, faceIndex)
                            face.Index = faceIndex
                            face.UnitsPerEm = typeface.UnitsPerEm
                            Dim font = New HB.Font(face)
                            font.SetScale(ShapingScale, ShapingScale)
                            font.SetFunctionsOpenType()
                            Return font
                        End Using
                    End Using
                End Using
            Catch ex As Exception
                DiagnosticLogService.LogException("TextShaper.CreateShapingFont", ex)
                Return Nothing
            End Try
        End Function

        ''' <summary>Formt eine Zeile. Zeilenumbrueche gehoeren nicht hinein, die trennen die
        ''' Aufrufer vorher.</summary>
        Friend Function Shape(font As SKFont, text As String, Optional spacing As Single = 0.0F) As ShapedText
            Dim result As New ShapedText()
            If font Is Nothing OrElse String.IsNullOrEmpty(text) Then Return result
            Dim shapingFont = GetShapingFont(font.Typeface)
            If shapingFont Is Nothing Then Return ShapeUnformed(font, text, spacing)

            Using buffer = New HB.Buffer()
                buffer.AddUtf16(text)
                buffer.GuessSegmentProperties()
                shapingFont.Shape(buffer, If(spacing <> 0.0F, _withoutLigatures, Array.Empty(Of HB.Feature)()))
                Dim infos = buffer.GlyphInfos
                Dim positions = buffer.GlyphPositions
                Dim count = infos.Length
                Dim factorY = font.Size / ShapingScale
                Dim factorX = factorY * font.ScaleX
                Allocate(result, count)
                Dim pen As Single = 0.0F
                Dim cluster = -1
                For i = 0 To count - 1
                    If i = 0 OrElse infos(i).Cluster <> infos(i - 1).Cluster Then
                        cluster += 1
                        If cluster > 0 Then pen += spacing
                    End If
                    result.Glyphs(i) = CUShort(infos(i).Codepoint And &HFFFFUI)
                    result.PenX(i) = pen
                    result.X(i) = pen + positions(i).XOffset * factorX
                    ' HarfBuzz zaehlt nach oben, Skia nach unten.
                    result.Y(i) = -positions(i).YOffset * factorY
                    result.Advance(i) = positions(i).XAdvance * factorX
                    result.ClusterIndex(i) = cluster
                    pen += result.Advance(i)
                Next
                result.ClusterCount = cluster + 1
                result.Width = pen
            End Using
            Return result
        End Function

        ''' <summary>Ersatzweg, wenn sich die Schriftdatei nicht oeffnen laesst: eine Glyphe je
        ''' Zeichen mit ihrem Vorschub, wie Skia es von sich aus tut.</summary>
        Private Function ShapeUnformed(font As SKFont, text As String, spacing As Single) As ShapedText
            Dim result As New ShapedText()
            Dim glyphs = font.GetGlyphs(text)
            Dim noPaint As SKPaint = Nothing
            Dim widths = font.GetGlyphWidths(text, noPaint)
            Dim count = Math.Min(glyphs.Length, widths.Length)
            Allocate(result, count)
            Dim pen As Single = 0.0F
            For i = 0 To count - 1
                If i > 0 Then pen += spacing
                result.Glyphs(i) = glyphs(i)
                result.PenX(i) = pen
                result.X(i) = pen
                result.Advance(i) = widths(i)
                result.ClusterIndex(i) = i
                pen += widths(i)
            Next
            result.ClusterCount = count
            result.Width = pen
            Return result
        End Function

        Private Sub Allocate(result As ShapedText, count As Integer)
            ReDim result.Glyphs(count - 1)
            ReDim result.X(count - 1)
            ReDim result.Y(count - 1)
            ReDim result.PenX(count - 1)
            ReDim result.Advance(count - 1)
            ReDim result.ClusterIndex(count - 1)
        End Sub

        ''' <summary>Vorschub einer Zeile, Zeichenabstand eingerechnet.</summary>
        Friend Function MeasureWidth(font As SKFont, text As String, Optional spacing As Single = 0.0F) As Single
            Return Shape(font, text, spacing).Width
        End Function

        ''' <summary>Zeichnet eine geformte Zeile mit dem Anfang auf (x, baseline).</summary>
        Friend Sub Draw(canvas As SKCanvas, shaped As ShapedText, font As SKFont, x As Single, baseline As Single, paint As SKPaint)
            If shaped Is Nothing Then Return
            DrawGlyphs(canvas, shaped, font, 0, shaped.Glyphs.Length, x, baseline, paint)
        End Sub

        ''' <summary>Formt und zeichnet in einem Schritt.</summary>
        Friend Sub DrawText(canvas As SKCanvas, text As String, x As Single, baseline As Single, font As SKFont, paint As SKPaint,
                            Optional spacing As Single = 0.0F)
            Draw(canvas, Shape(font, text, spacing), font, x, baseline, paint)
        End Sub

        ''' <summary>Zeichnet einen Ausschnitt der Glyphen; die Lage jeder Glyphe kommt aus der
        ''' Formung, verschoben um (originX, originY).</summary>
        Friend Sub DrawGlyphs(canvas As SKCanvas, shaped As ShapedText, font As SKFont, first As Integer, count As Integer,
                              originX As Single, originY As Single, paint As SKPaint)
            If canvas Is Nothing OrElse shaped Is Nothing OrElse count <= 0 Then Return
            Dim handle = GCHandle.Alloc(shaped.Glyphs, GCHandleType.Pinned)
            Try
                Dim start = handle.AddrOfPinnedObject()
                For i = first To Math.Min(shaped.Glyphs.Length, first + count) - 1
                    Using blob = SKTextBlob.Create(IntPtr.Add(start, i * 2), 2, SKTextEncoding.GlyphId, font, New SKPoint(0, 0))
                        If blob IsNot Nothing Then canvas.DrawText(blob, originX + shaped.X(i), originY + shaped.Y(i), paint)
                    End Using
                Next
            Finally
                handle.Free()
            End Try
        End Sub

        ''' <summary>Sichtbare Glyphenkanten der Zeile, relativ zu Zeilenanfang und Grundlinie.
        ''' Leer, wenn keine Glyphe etwas zeichnet (nur Leerzeichen).</summary>
        Friend Function MeasureInk(shaped As ShapedText, font As SKFont) As SKRect
            Dim ink = SKRect.Empty
            If shaped Is Nothing OrElse shaped.Glyphs.Length = 0 Then Return ink
            Dim bounds As SKRect() = Nothing
            Dim handle = GCHandle.Alloc(shaped.Glyphs, GCHandleType.Pinned)
            Try
                Dim noPaint As SKPaint = Nothing
                font.GetGlyphWidths(handle.AddrOfPinnedObject(), shaped.Glyphs.Length * 2, SKTextEncoding.GlyphId, bounds, noPaint)
            Finally
                handle.Free()
            End Try
            If bounds Is Nothing Then Return ink
            For i = 0 To Math.Min(bounds.Length, shaped.Glyphs.Length) - 1
                Dim glyphBounds = bounds(i)
                If glyphBounds.IsEmpty Then Continue For
                glyphBounds.Offset(shaped.X(i), shaped.Y(i))
                If ink.IsEmpty Then ink = glyphBounds Else ink.Union(glyphBounds)
            Next
            Return ink
        End Function

    End Module

End Namespace
