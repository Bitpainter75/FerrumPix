Imports System
Imports System.Collections.Generic
Imports System.IO
Imports System.Text
Imports System.Text.Json

Namespace Services

    ''' <summary>
    ''' Legt die Bearbeitung als Ganzes mit in die PSD - dieselbe Beschreibung, die auch in einer
    ''' .fpx steht. Photoshop, Affinity und GIMP überspringen den Block, für sie bleibt es eine
    ''' gewöhnliche Datei mit Bildebenen.
    '''
    ''' Wozu: der Export rechnet Text, Formen und Korrekturebenen in Bildpunkte, weil PSD sie je Art
    ''' in einem eigenen, kaum dokumentierten Datensatz führt. Für fremde Programme ist das richtig
    ''' so. Öffnet FerrumPix aber seine EIGENE Datei wieder, muss es sich damit nicht zufriedengeben:
    ''' liegt das Rezept bei, kommt alles zurück wie es war, und der Text lässt sich weiter tippen.
    '''
    ''' Der Block überlebt kein Speichern in einem fremden Programm. Das ist hinnehmbar - wer dort
    ''' weiterarbeitet, hat den Text ohnehin verändert; dann gilt wieder, was in den Bildpunkten steht.
    ''' </summary>
    Public NotInheritable Class PsdRecipeService

        Private Sub New()
        End Sub

        ''' Bildressourcen ab 4000 sind laut Format für Erweiterungen frei. Die Kennung allein genügt
        ''' nicht - andere Programme dürfen dieselbe Nummer benutzen -, deshalb steht die Signatur
        ''' zusätzlich in den Daten und wird vor dem Auswerten geprüft.
        Public Const ResourceId As Integer = 4000
        Private Const Signature As String = "FerrumPixPsdRecipe"
        Private Const CurrentVersion As Integer = 1

        ''' <summary>Obergrenze für die eingebetteten Ebeneninhalte. Darüber bleibt das Rezept weg und
        ''' die Datei verhält sich wie jede andere PSD - lieber keine Zugabe als eine Datei, die
        ''' unbemerkt um Hunderte Megabyte wächst.</summary>
        Private Const MaxAssetBytes As Long = 64L * 1024L * 1024L

        ''' <summary>Dieselbe Grenze beim LESEN. Sie muss über <see cref="MaxAssetBytes"/> liegen,
        ''' weil der Block die Inhalte als Base64 trägt (ein Drittel mehr) und das Rezept selbst
        ''' dazukommt; 128 MB lassen jede selbst geschriebene Datei durch und weisen alles darüber
        ''' ab. Eine Grenze nur beim Schreiben wäre die halbe Sicherung: gelesen wird auch, was ein
        ''' fremdes oder beschädigtes Programm dort abgelegt hat, und daraus entstehen Zeichenkette,
        ''' Base64-Umweg und Dateien im Temp-Ordner.</summary>
        Private Const MaxPayloadBytes As Long = 128L * 1024L * 1024L

        Private Class RecipeEnvelope
            Public Property Signature As String = ""
            Public Property Version As Integer
            Public Property Adjustments As ImageAdjustments
            ''' Binäre Ebeneninhalte (eingefügte Bilder, ausgeschnittene Auswahl-Ebenen) als Base64,
            ''' unter dem Namen, der im Rezept als ImagePath steht.
            Public Property Assets As New Dictionary(Of String, String)()
        End Class

        Private Shared ReadOnly JsonOptions As New JsonSerializerOptions With {
            .WriteIndented = False,
            .IncludeFields = True,
            .PropertyNameCaseInsensitive = True
        }

        ''' <summary>Baut den Block. Nothing, wenn es nichts mitzugeben gibt oder die Inhalte zu groß
        ''' sind - dann wird schlicht keiner geschrieben.</summary>
        Public Shared Function Build(adj As ImageAdjustments) As Byte()
            If adj Is Nothing Then Return Nothing

            Try
                ' Auf einer Kopie arbeiten: die Bildpfade werden auf Namen im Block umgeschrieben,
                ' ohne die im Editor lebende Bearbeitung anzufassen - dieselbe Regel wie beim .fpx.
                Dim copy = adj.Clone()
                Dim envelope As New RecipeEnvelope With {
                    .Signature = Signature,
                    .Version = CurrentVersion,
                    .Adjustments = copy
                }

                Dim total As Long = 0
                Dim seen As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)
                If copy.Annotations IsNot Nothing Then
                    For Each ann In copy.Annotations
                        If ann Is Nothing OrElse String.IsNullOrWhiteSpace(ann.ImagePath) Then Continue For
                        ' avares://-Symbole bleiben als Verweis stehen, sie liegen im Programm selbst.
                        If Not File.Exists(ann.ImagePath) Then Continue For

                        Dim assetName As String = Nothing
                        If Not seen.TryGetValue(ann.ImagePath, assetName) Then
                            Dim bytes = File.ReadAllBytes(ann.ImagePath)
                            total += bytes.Length
                            If total > MaxAssetBytes Then Return Nothing
                            assetName = "a" & seen.Count.ToString() & Path.GetExtension(ann.ImagePath)
                            seen(ann.ImagePath) = assetName
                            envelope.Assets(assetName) = Convert.ToBase64String(bytes)
                        End If
                        ann.ImagePath = assetName
                    Next
                End If

                Dim json = JsonSerializer.Serialize(envelope, JsonOptions)
                Return Encoding.UTF8.GetBytes(json)
            Catch
                Return Nothing
            End Try
        End Function

        ''' <summary>Liest den Block zurück und schreibt die Ebeneninhalte in
        ''' <paramref name="assetDir"/>. Nothing, wenn kein eigener Block vorliegt.</summary>
        Public Shared Function Parse(payload As Byte(), assetDir As String) As ImageAdjustments
            If payload Is Nothing OrElse payload.Length < 16 Then Return Nothing
            ' Auch hier deckeln und nicht nur beim Herausholen: Parse ist öffentlich, und der
            ' nächste Aufrufer kommt vielleicht anderswo her.
            If payload.LongLength > MaxPayloadBytes Then Return Nothing

            Try
                Dim json = Encoding.UTF8.GetString(payload)
                Dim envelope = JsonSerializer.Deserialize(Of RecipeEnvelope)(json, JsonOptions)
                If envelope Is Nothing OrElse envelope.Adjustments Is Nothing Then Return Nothing
                ' Die Signatur ist der eigentliche Beweis, nicht die Ressourcennummer.
                If Not String.Equals(envelope.Signature, Signature, StringComparison.Ordinal) Then Return Nothing
                ' Ein neueres Format als das eigene wird nicht geraten, sondern ausgelassen.
                If envelope.Version > CurrentVersion Then Return Nothing

                Directory.CreateDirectory(assetDir)
                Dim written As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)
                If envelope.Assets IsNot Nothing Then
                    For Each kv In envelope.Assets
                        Dim safeName = Path.GetFileName(kv.Key)
                        If String.IsNullOrWhiteSpace(safeName) Then Continue For
                        Dim target = Path.Combine(assetDir, safeName)
                        File.WriteAllBytes(target, Convert.FromBase64String(kv.Value))
                        written(kv.Key) = target
                    Next
                End If

                If envelope.Adjustments.Annotations IsNot Nothing Then
                    For Each ann In envelope.Adjustments.Annotations
                        If ann Is Nothing OrElse String.IsNullOrWhiteSpace(ann.ImagePath) Then Continue For
                        Dim target As String = Nothing
                        If written.TryGetValue(ann.ImagePath, target) Then ann.ImagePath = target
                    Next
                End If

                Return envelope.Adjustments
            Catch
                Return Nothing
            End Try
        End Function

        ''' <summary>Sucht den eigenen Block in den Bildressourcen der Datei. Nothing, wenn keiner
        ''' da ist - dann kommt die Datei wie jede fremde PSD als Stapel Bildebenen herein.</summary>
        Public Shared Function ExtractPayload(psdPath As String) As Byte()
            Try
                Using fs = File.OpenRead(psdPath)
                    Dim head(25) As Byte
                    If fs.Read(head, 0, 26) <> 26 Then Return Nothing
                    If head(0) <> Asc("8") OrElse head(1) <> Asc("B") OrElse head(2) <> Asc("P") OrElse head(3) <> Asc("S") Then Return Nothing

                    Dim colorModeLen = ReadU32(fs)
                    If colorModeLen < 0 OrElse fs.Position + colorModeLen > fs.Length Then Return Nothing
                    fs.Seek(colorModeLen, SeekOrigin.Current)

                    Dim resourcesLen = ReadU32(fs)
                    Dim resourcesEnd = fs.Position + resourcesLen
                    If resourcesEnd > fs.Length Then Return Nothing

                    While fs.Position + 12 <= resourcesEnd
                        Dim sig(3) As Byte
                        If fs.Read(sig, 0, 4) <> 4 Then Return Nothing
                        If Encoding.ASCII.GetString(sig) <> "8BIM" Then Return Nothing
                        Dim id = ReadU16(fs)
                        ' Pascal-Name: Längenbyte plus Text, das ganze Feld auf gerade Länge gefüllt.
                        Dim nameLen = fs.ReadByte()
                        If nameLen < 0 Then Return Nothing
                        Dim namePadded = If((nameLen + 1) Mod 2 = 0, nameLen, nameLen + 1)
                        fs.Seek(namePadded, SeekOrigin.Current)
                        Dim dataLen = ReadU32(fs)
                        Dim dataStart = fs.Position
                        If dataStart + dataLen > resourcesEnd Then Return Nothing

                        ' Zu grosse Bloecke werden UEBERSPRUNGEN, nicht gelesen: die Nummer ab 4000
                        ' steht jedem frei, ein Riesenblock dort ist also eher ein fremder als ein
                        ' eigener - und selbst wenn er eigen waere, kaeme er aus einer Fassung, die
                        ' den Deckel beim Schreiben nicht hatte.
                        If id = ResourceId AndAlso dataLen > 0 AndAlso dataLen <= MaxPayloadBytes Then
                            Dim buffer(CInt(dataLen) - 1) As Byte
                            If fs.Read(buffer, 0, buffer.Length) = buffer.Length Then Return buffer
                            Return Nothing
                        End If

                        fs.Seek(dataStart + dataLen + (dataLen And 1L), SeekOrigin.Begin)
                    End While
                End Using
            Catch
            End Try
            Return Nothing
        End Function

        Private Shared Function ReadU16(fs As Stream) As Integer
            Dim b(1) As Byte
            If fs.Read(b, 0, 2) <> 2 Then Throw New EndOfStreamException()
            Return CInt(b(0)) * 256 + b(1)
        End Function

        ' Vor dem Schieben weiten, sonst bliebe der Ausdruck in VB ein Byte und ergäbe still 0.
        Private Shared Function ReadU32(fs As Stream) As Long
            Dim b(3) As Byte
            If fs.Read(b, 0, 4) <> 4 Then Throw New EndOfStreamException()
            Return (CLng(b(0)) << 24) Or (CLng(b(1)) << 16) Or (CLng(b(2)) << 8) Or CLng(b(3))
        End Function

    End Class

End Namespace
