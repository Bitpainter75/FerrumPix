Imports System
Imports System.Collections.Concurrent
Imports System.Collections.Generic
Imports System.IO
Imports System.Linq
Imports System.Xml
Imports System.Xml.Linq

Namespace Services

    ''' <summary>
    ''' Mehrere Varianten einer Aufnahme in EINER Begleitdatei.
    '''
    ''' Die Standardvariante ist die Datei selbst: ihr Rezept steht, wo es immer stand (Element
    ''' "adjustments"), ihre Katalogdaten im Block "catalog". Jede zusaetzliche Variante liegt im
    ''' Block "variants" daneben und traegt alles Eigene selbst:
    '''
    '''   variants
    '''     variant id="9b1e44c0" name="Schwarzweiss" edited="true"
    '''       catalog        (Bewertung, Favorit, Etikett, Markierung, Stichwoerter dieser Variante)
    '''       adjustments    (Rezept-JSON als CDATA)
    '''
    ''' Die Kennung ist fest und aendert sich beim Umbenennen nicht: an ihr haengen die Kachel, der
    ''' Kachelspeicher und die Zeilen des Katalogs (siehe <see cref="VariantKey"/>). Alle
    ''' oeffentlichen Lese- und Schreibwege dieses Dienstes nehmen statt eines Pfades auch einen
    ''' solchen Schluessel und meinen dann Rezept und Katalogblock DIESER Variante.
    '''
    ''' Es gibt keine zweite Datei: Umbenennen, Verschieben, Kopieren, Loeschen und der Weg zu einem
    ''' Server nehmen alle Varianten mit, ohne davon zu wissen. Eine Datei ohne zusaetzliche Variante
    ''' traegt den Block nicht und sieht aus wie vorher.
    ''' </summary>
    Partial Public NotInheritable Class RawSidecarService

        Private Const VariantsElement As String = "variants"
        Private Const VariantElement As String = "variant"
        Private Const VariantIdAttribute As String = "id"
        Private Const VariantNameAttribute As String = "name"

        ''' <summary>Laenger wird kein Name: er steht unter der Kachel und in einem Dateinamen.</summary>
        Public Const MaxVariantNameLength As Integer = 60

        Friend NotInheritable Class VariantEntry
            Public Id As String
            Public Name As String
            Public Json As String
            Public Edited As Boolean
            ''' Der Katalogblock dieser Variante, wie er in der Datei steht; Nothing ohne einen.
            Public Catalog As XElement
        End Class

        Friend NotInheritable Class VariantSet
            Public ReadOnly Entries As New List(Of VariantEntry)()

            Public Function Find(id As String) As VariantEntry
                Return Entries.FirstOrDefault(Function(e) String.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase))
            End Function
        End Class

        ''' <summary>Eine zusaetzliche Variante, wie die Oberflaeche sie kennt.</summary>
        Public NotInheritable Class SidecarVariant
            Public Sub New(id As String, name As String)
                Me.Id = id
                Me.Name = name
            End Sub

            Public ReadOnly Property Id As String
            Public ReadOnly Property Name As String
        End Class

        Private NotInheritable Class CachedVariantList
            Public StampTicks As Long
            Public Variants As IReadOnlyList(Of SidecarVariant)
        End Class

        Private Shared ReadOnly _variantListCache As New ConcurrentDictionary(Of String, CachedVariantList)(PathIdentity.Comparer)
        Private Shared ReadOnly NoVariants As IReadOnlyList(Of SidecarVariant) = New List(Of SidecarVariant)()

        ''' <summary>Schneidet einen eingegebenen Namen zu. Leer heisst: kein gueltiger Name.</summary>
        Public Shared Function NormalizeVariantName(name As String) As String
            Dim trimmed = String.Join(" ", If(name, "").Split(New Char() {" "c, ControlChars.Tab, ControlChars.Cr, ControlChars.Lf},
                                                              StringSplitOptions.RemoveEmptyEntries))
            If trimmed.Length > MaxVariantNameLength Then trimmed = trimmed.Substring(0, MaxVariantNameLength).TrimEnd()
            Return trimmed
        End Function

        ''' <summary>Die zusaetzlichen Varianten einer Datei, in der Reihenfolge der Datei. Nie
        ''' Nothing; leer bei fehlender oder unlesbarer Begleitdatei und bei einer ohne Block.
        '''
        ''' Der Aufruf sitzt im Aufbau der Ordnerliste, einmal je Datei mit Begleitdatei. Deshalb
        ''' nach Zeitstempel gemerkt, und eine Datei ohne Block wird nicht geparst: ihre
        ''' Formatnummer steht in den ersten Zeichen.</summary>
        Public Shared Function ReadVariants(rawPath As String) As IReadOnlyList(Of SidecarVariant)
            If String.IsNullOrWhiteSpace(rawPath) Then Return NoVariants
            Dim sidecar = SidecarPathFor(rawPath)
            Dim stampTicks As Long
            Try
                If Not File.Exists(sidecar) Then Return NoVariants
                stampTicks = File.GetLastWriteTimeUtc(sidecar).Ticks
            Catch
                Return NoVariants
            End Try
            Dim cached As CachedVariantList = Nothing
            If _variantListCache.TryGetValue(sidecar, cached) AndAlso cached.StampTicks = stampTicks Then Return cached.Variants

            Dim found = NoVariants
            Try
                If HeaderNamesVariants(sidecar) Then
                    Dim parsed = ReadVariantSet(rawPath)
                    If parsed IsNot Nothing AndAlso parsed.Entries.Count > 0 Then
                        found = parsed.Entries.Select(Function(e) New SidecarVariant(e.Id, e.Name)).ToList()
                    End If
                End If
            Catch
                found = NoVariants
            End Try
            _variantListCache(sidecar) = New CachedVariantList With {.StampTicks = stampTicks, .Variants = found}
            Return found
        End Function

        ''' <summary>Steht in den ersten Zeichen die Formatnummer einer Datei mit Varianten?</summary>
        Private Shared Function HeaderNamesVariants(sidecar As String) As Boolean
            Dim buffer(511) As Char
            Dim count As Integer
            Using reader = New StreamReader(sidecar)
                count = reader.ReadBlock(buffer, 0, buffer.Length)
            End Using
            Dim head = New String(buffer, 0, count)
            Return head.Contains("version=""" & VariantsFormatVersion.ToString(Globalization.CultureInfo.InvariantCulture) & """",
                                 StringComparison.Ordinal)
        End Function

        ''' <summary>Der Name der Variante hinter einem Schluessel; leer bei einem gewoehnlichen Pfad
        ''' und bei einer Variante, die es nicht (mehr) gibt.</summary>
        Public Shared Function VariantName(key As String) As String
            Dim id As String = Nothing
            Dim file = VariantKey.Split(key, id)
            If id.Length = 0 Then Return ""
            Dim found = ReadVariants(file).FirstOrDefault(Function(v) String.Equals(v.Id, id, StringComparison.OrdinalIgnoreCase))
            Return If(found Is Nothing, "", found.Name)
        End Function

        ''' <summary>Gibt es, was dieser Schluessel meint? Bei einem gewoehnlichen Pfad die Datei,
        ''' bei einer Variante zusaetzlich ihren Eintrag in der Begleitdatei.</summary>
        Public Shared Function VariantExists(key As String) As Boolean
            Dim id As String = Nothing
            Dim file = VariantKey.Split(key, id)
            If String.IsNullOrWhiteSpace(file) OrElse Not IO.File.Exists(file) Then Return False
            If id.Length = 0 Then Return True
            Return ReadVariants(file).Any(Function(v) String.Equals(v.Id, id, StringComparison.OrdinalIgnoreCase))
        End Function

        ''' <summary>Legt eine weitere Variante an.</summary>
        ''' <param name="sourceKey">Die Datei oder die Variante, von der die neue ausgeht. Ihre
        ''' Katalogdaten gehen immer mit, ihr Rezept nur mit <paramref name="fromCurrent"/>.</param>
        ''' <param name="name">Der Name der neuen Variante.</param>
        ''' <param name="reservedName">Der Name der Standardvariante; so darf keine zusaetzliche
        ''' heissen.</param>
        ''' <param name="fromCurrent">True uebernimmt das Rezept der Quelle, False beginnt bei den
        ''' Startwerten der Datei.</param>
        ''' <param name="createWhenMissing">Das Rezept der Standardvariante, wenn es noch keine
        ''' Begleitdatei gibt.</param>
        ''' <param name="newKey">Der Schluessel der neuen Variante, wenn geschrieben wurde.</param>
        Public Shared Function TryAddVariant(sourceKey As String, name As String, reservedName As String,
                                             fromCurrent As Boolean,
                                             createWhenMissing As Func(Of ImageAdjustments),
                                             ByRef newKey As String) As ModifyRecipeResult
            newKey = Nothing
            Dim newName = NormalizeVariantName(name)
            Dim sourceId As String = Nothing
            Dim file = VariantKey.Split(sourceKey, sourceId)
            If newName.Length = 0 OrElse String.IsNullOrWhiteSpace(file) Then Return ModifyRecipeResult.Failed
            If String.Equals(newName, NormalizeVariantName(reservedName), StringComparison.OrdinalIgnoreCase) Then Return ModifyRecipeResult.Failed
            Dim createdId As String = Nothing
            Try
                SyncLock _writeLock
                    ' Auch gegen andere Prozesse: zwischen dem Anlegen der Standardvariante und dem
                    ' Eintragen der neuen darf niemand dazwischen schreiben.
                    Using crossLock = LockAcrossProcesses(file)
                        If crossLock Is Nothing Then Return ModifyRecipeResult.Failed
                        ' Ohne Begleitdatei entsteht zuerst die der Standardvariante, wie bei jedem
                        ' anderen ersten Schreiben.
                        If ProbeSidecar(SidecarPathFor(file)) = SidecarReadResult.Missing Then
                            If sourceId.Length > 0 OrElse createWhenMissing Is Nothing Then Return ModifyRecipeResult.Failed
                            Dim first = createWhenMissing()
                            If first Is Nothing Then Return ModifyRecipeResult.Failed
                            If Not TryWriteCore(file, first, LibraryService.Instance.GetSidecarCatalogData(file), Nothing,
                                                IsEditedRecipe(file, first), requireAbsent:=True) Then Return ModifyRecipeResult.Failed
                        End If
                        Dim result = ModifyVariantsDocument(file,
                            Function(root, variants)
                                If NameTaken(variants.Entries, newName, Nothing) Then Return False
                                Dim entry As New VariantEntry With {.Id = VariantKey.NewId(), .Name = newName}
                                While variants.Find(entry.Id) IsNot Nothing
                                    entry.Id = VariantKey.NewId()
                                End While
                                Dim source = If(sourceId.Length = 0, Nothing, variants.Find(sourceId))
                                If sourceId.Length > 0 AndAlso source Is Nothing Then Return False
                                Dim sourceCatalog = If(source Is Nothing, root.Element(Ns + "catalog"), source.Catalog)
                                If sourceCatalog IsNot Nothing Then entry.Catalog = New XElement(sourceCatalog)
                                If fromCurrent Then
                                    entry.Json = If(source Is Nothing, root.Element(Ns + "adjustments")?.Value, source.Json)
                                    If source IsNot Nothing Then
                                        entry.Edited = source.Edited
                                    Else
                                        Dim editedValue = root.Attribute(EditedAttribute)?.Value
                                        entry.Edited = If(editedValue Is Nothing, IsEditedFile(file),
                                                          String.Equals(editedValue, "true", StringComparison.OrdinalIgnoreCase))
                                    End If
                                Else
                                    Dim start = StartValuesFor(file)
                                    start.BakedOperationsApplied = False
                                    entry.Json = FpxService.SerializeAdjustments(start)
                                    entry.Edited = False
                                End If
                                If String.IsNullOrWhiteSpace(entry.Json) Then Return False
                                variants.Entries.Add(entry)
                                createdId = entry.Id
                                ' Zwei Varianten derselben Aufnahme saehen in der Galerie gleich aus,
                                ' solange dort die Vorschau der Kamera steht.
                                root.SetAttributeValue(DevelopedThumbnailAttribute, "true")
                                Return True
                            End Function)
                        If result = ModifyRecipeResult.Written Then newKey = VariantKey.Make(file, createdId)
                        Return result
                    End Using
                End SyncLock
            Catch ex As Exception
                DiagnosticLogService.LogException("Sidecar.Variants", ex)
                Return ModifyRecipeResult.Failed
            End Try
        End Function

        ''' <summary>Benennt die Variante hinter dem Schluessel um. Die Standardvariante hat keinen
        ''' eigenen Namen und laesst sich nicht umbenennen.</summary>
        Public Shared Function TryRenameVariant(key As String, name As String, reservedName As String) As ModifyRecipeResult
            Dim newName = NormalizeVariantName(name)
            Dim id As String = Nothing
            Dim file = VariantKey.Split(key, id)
            If newName.Length = 0 OrElse id.Length = 0 Then Return ModifyRecipeResult.Failed
            If String.Equals(newName, NormalizeVariantName(reservedName), StringComparison.OrdinalIgnoreCase) Then Return ModifyRecipeResult.Failed
            Return ModifyVariantsDocument(file,
                Function(root, variants)
                    Dim entry = variants.Find(id)
                    If entry Is Nothing OrElse NameTaken(variants.Entries, newName, entry) Then Return False
                    entry.Name = newName
                    Return True
                End Function)
        End Function

        ''' <summary>Loescht die Variante hinter dem Schluessel. Bleibt keine zusaetzliche uebrig,
        ''' faellt der Block weg, und die Datei sieht wieder aus wie eine ohne Varianten.
        '''
        ''' DIE STANDARDVARIANTE (ein gewoehnlicher Pfad) laesst sich loeschen, solange es eine
        ''' zusaetzliche gibt: die Datei bleibt, und die erste zusaetzliche Variante RUECKT AUF. Ihr
        ''' Rezept, ihr Vermerk und ihre Katalogdaten stehen danach dort, wo die der Standardvariante
        ''' standen; ihren Namen und ihre Kennung gibt es nicht mehr. Ohne zusaetzliche Variante ist
        ''' die Standardvariante die Datei selbst, und die loescht dieser Dienst nicht.</summary>
        ''' <param name="promotedKey">Der bisherige Schluessel der aufgerueckten Variante, wenn die
        ''' Standardvariante geloescht wurde.</param>
        Public Shared Function TryDeleteVariant(key As String, ByRef promotedKey As String) As ModifyRecipeResult
            promotedKey = Nothing
            Dim id As String = Nothing
            Dim file = VariantKey.Split(key, id)
            If id.Length = 0 Then
                Dim promotedId As String = Nothing
                Dim promoted = ModifyVariantsDocument(file,
                    Function(root, variants)
                        If variants.Entries.Count = 0 Then Return False
                        Dim first = variants.Entries(0)
                        Dim adjustmentsNode = root.Element(Ns + "adjustments")
                        If adjustmentsNode Is Nothing Then Return False
                        adjustmentsNode.ReplaceNodes(New XCData(first.Json))
                        root.SetAttributeValue(EditedAttribute, If(first.Edited, "true", "false"))
                        ' Der Katalogblock der aufgerueckten Variante, an der Stelle des alten. Ohne
                        ' eigenen ein leerer: sonst gaelten die Angaben der geloeschten weiter.
                        Dim catalogBlock = If(first.Catalog Is Nothing, CatalogNode(Nothing), New XElement(first.Catalog))
                        Dim oldCatalog = root.Element(Ns + "catalog")
                        If oldCatalog IsNot Nothing Then
                            oldCatalog.ReplaceWith(catalogBlock)
                        Else
                            adjustmentsNode.AddBeforeSelf(catalogBlock)
                        End If
                        variants.Entries.RemoveAt(0)
                        promotedId = first.Id
                        Return True
                    End Function)
                If promoted = ModifyRecipeResult.Written Then promotedKey = VariantKey.Make(file, promotedId)
                Return promoted
            End If
            Return ModifyVariantsDocument(file,
                Function(root, variants)
                    Dim entry = variants.Find(id)
                    If entry Is Nothing Then Return False
                    variants.Entries.Remove(entry)
                    Return True
                End Function)
        End Function

        ''' <summary>Wie die Fassung mit <c>promotedKey</c>, fuer Aufrufer, die nicht wissen muessen,
        ''' welche Variante aufgerueckt ist.</summary>
        Public Shared Function TryDeleteVariant(key As String) As ModifyRecipeResult
            Dim ignored As String = Nothing
            Return TryDeleteVariant(key, ignored)
        End Function

        Private Shared Function NameTaken(entries As List(Of VariantEntry), name As String, except As VariantEntry) As Boolean
            Return entries.Any(Function(e) e IsNot except AndAlso String.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase))
        End Function

        ''' <summary>Schreibt Rezept und Vermerk EINER zusaetzlichen Variante; alles andere in der
        ''' Datei bleibt, wie es ist. Nothing laesst den jeweiligen Teil stehen. Zu
        ''' <paramref name="guard"/> siehe <see cref="ModifyVariantsDocument"/>.</summary>
        Private Shared Function WriteVariantRecipe(file As String, id As String, adjustments As ImageAdjustments,
                                                   edited As Boolean?,
                                                   Optional guard As SidecarStamp = Nothing) As ModifyRecipeResult
            If adjustments Is Nothing Then Return ModifyRecipeResult.Failed
            ' Wie beim Schreiben der Standardvariante: neben der Datei liegt kein Pixel, gebackene
            ' Vorgaenge sind also noch anzuwenden.
            Dim stored = adjustments.Clone()
            stored.BakedOperationsApplied = False
            Dim json = FpxService.SerializeAdjustments(stored)
            Return ModifyVariantsDocument(file,
                Function(root, variants)
                    Dim entry = variants.Find(id)
                    If entry Is Nothing Then Return False
                    entry.Json = json
                    If edited.HasValue Then entry.Edited = edited.Value
                    Return True
                End Function, guard)
        End Function

        ''' <summary>Schreibt den Katalogblock EINER zusaetzlichen Variante.</summary>
        Private Shared Function WriteVariantCatalog(file As String, id As String, catalog As RawSidecarCatalogData) As Boolean
            Return ModifyVariantsDocument(file,
                Function(root, variants)
                    Dim entry = variants.Find(id)
                    If entry Is Nothing Then Return False
                    entry.Catalog = CatalogNode(catalog)
                    Return True
                End Function) = ModifyRecipeResult.Written
        End Function

        ''' <summary>Die Begleitdatei lesen, ihre Varianten aendern und sie zurueckschreiben, unter
        ''' der Schreibsperre und am Dokument selbst: was <paramref name="change"/> nicht anfasst
        ''' (Rezept und Katalog der Standardvariante, Beschreibung, Uebernommenes), bleibt Zeichen
        ''' fuer Zeichen stehen. <paramref name="change"/> sagt mit False, dass nichts zu schreiben
        ''' ist. Eine fehlende Datei ist hier ein Fehlschlag: eine Variante ohne Standardvariante
        ''' gibt es nicht. <paramref name="guard"/> wie bei TryWriteCore: weicht die Platte vor dem
        ''' Umbenennen davon ab, wird nichts ersetzt (Failed).</summary>
        Private Shared Function ModifyVariantsDocument(file As String,
                                                       change As Func(Of XElement, VariantSet, Boolean),
                                                       Optional guard As SidecarStamp = Nothing) As ModifyRecipeResult
            If String.IsNullOrWhiteSpace(file) OrElse change Is Nothing Then Return ModifyRecipeResult.Failed
            Dim temp As String = Nothing
            Try
                SyncLock _writeLock
                    Using crossLock = LockAcrossProcesses(file)
                        If crossLock Is Nothing Then Return ModifyRecipeResult.Failed
                        Dim target = SidecarPathFor(file)
                        Select Case ProbeSidecar(target)
                            Case SidecarReadResult.Missing : Return ModifyRecipeResult.Failed
                            Case SidecarReadResult.Unreadable : Return ModifyRecipeResult.Unreadable
                        End Select
                        ' Dieselbe Pruefung wie jeder Leser: eine Datei, die wir nicht verstehen, wird
                        ' nicht angefasst.
                        Dim ignored As ImageAdjustments = Nothing
                        If ReadRecipe(file, ignored) <> SidecarReadResult.Read Then Return ModifyRecipeResult.Unreadable
                        Dim doc = XDocument.Load(target)
                        Dim root = doc.Root
                        Dim variants As VariantSet = Nothing
                        If Not TryParseVariants(root, variants) Then Return ModifyRecipeResult.Unreadable
                        If variants Is Nothing Then variants = New VariantSet()
                        If Not change(root, variants) Then Return ModifyRecipeResult.Failed
                        root.Element(Ns + VariantsElement)?.Remove()
                        root.Element(Ns + "versions")?.Remove()
                        Dim block = VariantsNode(variants)
                        If block IsNot Nothing Then root.Add(block)
                        root.SetAttributeValue("version", If(block Is Nothing, FormatVersion, VariantsFormatVersion))
                        root.SetElementValue(Ns + "savedUtc", DateTime.UtcNow.ToString("O"))

                        Dim finalTarget = ImageProcessor.ResolveWriteTarget(target)
                        temp = ImageProcessor.AtomicSiblingPath(finalTarget)
                        Using fs = New FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)
                            Using writer = XmlWriter.Create(fs, New XmlWriterSettings With {.Indent = True})
                                doc.Save(writer)
                            End Using
                            fs.Flush(flushToDisk:=True)
                        End Using
                        ImageProcessor.CarryOverFileMode(finalTarget, temp)
                        ' Wie bei der Standardvariante (TryWriteCore): hat ein anderes Programm seit
                        ' dem Stand des Aufrufers geschrieben, wird nichts ersetzt.
                        If guard IsNot Nothing AndAlso Not CaptureStamp(file, parse:=False).SameContentAs(guard) Then
                            DiagnosticLogService.LogAlways("Sidecar.Variants",
                                $"vor dem Umbenennen von aussen geaendert, nicht ersetzt: {IO.Path.GetFileName(target)}")
                            IO.File.Delete(temp)
                            Return ModifyRecipeResult.Failed
                        End If
                        IO.File.Move(temp, finalTarget, overwrite:=True)
                        ForgetDisplayFacts(target)
                        Return ModifyRecipeResult.Written
                    End Using
                End SyncLock
            Catch ex As Exception
                DiagnosticLogService.LogException("Sidecar.Variants", ex)
                Try
                    If temp IsNot Nothing AndAlso IO.File.Exists(temp) Then IO.File.Delete(temp)
                Catch
                End Try
                Return ModifyRecipeResult.Failed
            End Try
        End Function

        ''' <summary>Der Block fuer die Datei. Nothing ohne zusaetzliche Variante.</summary>
        Private Shared Function VariantsNode(variants As VariantSet) As XElement
            If variants Is Nothing OrElse variants.Entries.Count = 0 Then Return Nothing
            Dim node = New XElement(Ns + VariantsElement)
            For Each entry In variants.Entries
                node.Add(New XElement(Ns + VariantElement,
                                      New XAttribute(VariantIdAttribute, entry.Id),
                                      New XAttribute(VariantNameAttribute, entry.Name),
                                      New XAttribute(EditedAttribute, If(entry.Edited, "true", "false")),
                                      If(entry.Catalog Is Nothing, Nothing, New XElement(entry.Catalog)),
                                      New XElement(Ns + "adjustments",
                                                   New XAttribute("format", "fpx-json"),
                                                   New XCData(entry.Json))))
            Next
            Return node
        End Function

        ''' <summary>Liest den Block aus dem Wurzelelement. Ohne Block ist das Ergebnis Nothing und
        ''' der Rueckgabewert True. False heisst: da ist ein Block, den wir nicht verstehen (eine
        ''' Variante ohne Kennung, ohne Namen oder ohne Rezept, eine Kennung doppelt). Die Datei gilt
        ''' dann als unlesbar, damit niemand sie ohne den Block zurueckschreibt.</summary>
        Private Shared Function TryParseVariants(root As XElement, ByRef variants As VariantSet) As Boolean
            variants = Nothing
            ' Auch der Blockname "versions" aus einer Vorabfassung, siehe unten bei den Eintraegen.
            Dim node = If(root?.Element(Ns + VariantsElement), root?.Element(Ns + "versions"))
            If node Is Nothing Then Return True
            Dim parsed As New VariantSet()
            ' WAS WIR NICHT KENNEN, MACHT DEN BLOCK UNLESBAR, statt uebergangen zu werden. Der Block
            ' wird beim Schreiben aus den gelesenen Eintraegen NEU gebaut (VariantsNode); ein
            ' uebergangenes Element, etwa aus einer neueren Fassung, waere beim naechsten Speichern
            ' still verloren. Als unlesbar laesst jeder Schreiber die Datei stehen.
            If node.Attributes().Any(Function(a) Not a.IsNamespaceDeclaration) Then Return False
            ' Auch der Elementname "version": so schrieb eine Vorabfassung die Eintraege, bevor sie
            ' Varianten hiessen. Gelesen wird beides, geschrieben nur noch "variant"; beim naechsten
            ' Schreiben traegt die Datei den neuen Namen.
            If node.Elements().Any(Function(e) e.Name <> Ns + VariantElement AndAlso e.Name <> Ns + "version") Then Return False
            For Each child In node.Elements()
                If child.Elements().Any(Function(e) e.Name <> Ns + "adjustments" AndAlso e.Name <> Ns + "catalog") OrElse
                   child.Elements(Ns + "adjustments").Count() > 1 OrElse child.Elements(Ns + "catalog").Count() > 1 Then Return False
                If child.Attributes().Any(Function(a) Not a.IsNamespaceDeclaration AndAlso
                                                      a.Name <> VariantIdAttribute AndAlso a.Name <> VariantNameAttribute AndAlso
                                                      a.Name <> EditedAttribute) Then Return False
                ' Auch das Rezept selbst: VariantsNode schreibt es neu als
                ' <adjustments format="fpx-json"> mit dem Text darin. Ein weiteres Attribut, ein
                ' anderes Format oder ein Kindelement ginge dabei verloren.
                Dim adjustmentsNode = child.Element(Ns + "adjustments")
                If adjustmentsNode IsNot Nothing Then
                    If adjustmentsNode.Attributes().Any(Function(a) Not a.IsNamespaceDeclaration AndAlso a.Name <> "format") Then Return False
                    If Not String.Equals(adjustmentsNode.Attribute("format")?.Value, "fpx-json", StringComparison.Ordinal) Then Return False
                    If adjustmentsNode.Elements().Any() Then Return False
                End If
                Dim id = If(child.Attribute(VariantIdAttribute)?.Value, "").Trim()
                Dim name = NormalizeVariantName(child.Attribute(VariantNameAttribute)?.Value)
                Dim json = child.Element(Ns + "adjustments")?.Value
                If Not VariantKey.IsVariant(VariantKey.Make("x", id)) OrElse name.Length = 0 OrElse
                   String.IsNullOrWhiteSpace(json) OrElse parsed.Find(id) IsNot Nothing Then Return False
                parsed.Entries.Add(New VariantEntry With {
                    .Id = id,
                    .Name = name,
                    .Json = json,
                    .Edited = String.Equals(child.Attribute(EditedAttribute)?.Value, "true", StringComparison.OrdinalIgnoreCase),
                    .Catalog = child.Element(Ns + "catalog")})
            Next
            If parsed.Entries.Count = 0 Then Return False
            variants = parsed
            Return True
        End Function

        ''' <summary>Der Block der vorhandenen Datei. Nothing, wenn sie fehlt, nicht lesbar ist oder
        ''' keinen traegt.</summary>
        Private Shared Function ReadVariantSet(rawPath As String) As VariantSet
            If String.IsNullOrWhiteSpace(rawPath) Then Return Nothing
            Dim sidecar = SidecarPathFor(rawPath)
            If ProbeSidecar(sidecar) <> SidecarReadResult.Read Then Return Nothing
            Try
                Dim root = XDocument.Load(sidecar).Root
                If root Is Nothing OrElse root.Name <> Ns + "recipe" Then Return Nothing
                Dim variants As VariantSet = Nothing
                Return If(TryParseVariants(root, variants), variants, Nothing)
            Catch
                Return Nothing
            End Try
        End Function

    End Class

End Namespace
