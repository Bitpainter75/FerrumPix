Imports SkiaSharp

Namespace Services

    ''' <summary>
    ''' Das ARBEITSBILD des Editors (Umbau 2026-07-17, Plan Stufen A-G, siehe
    ''' EDITOR_RENDERING_NOTES.md): das voll aufgelöste Bild, in das Retusche,
    ''' Pinsel-/Radiererstriche und gerasterte Ebenen einmalig REGIONAL eingebacken werden,
    ''' statt sie bei jedem Render aus dem Rezept neu abzuspielen. Die Editor-Vorschau ist
    ''' eine Herunterskalierung dieses Bildes; Export/Zoom-Detail lesen es direkt.
    '''
    ''' Besitzverhältnisse: der Service besitzt das VOLL-Bitmap. Das VORSCHAU-Bitmap geht bei
    ''' Init in den Besitz des EditorViewModels über (dessen _previewSource-/Stale-Mechanik
    ''' entsorgt es beim Bildwechsel); der Service behält nur eine Referenz, um ab Stufe D
    ''' Commit-Regionen nachzuziehen.
    '''
    ''' Stand Stufe A: Skelett ohne Commits/Undo-Patches - Verhalten des Editors unverändert,
    ''' die Vorschau entspricht exakt ImageProcessor.LoadPreviewSource (gleiche Skalierung).
    ''' </summary>
    ''' <summary>Regionaler VORHER-Ausschnitt eines Commits für das Undo (Tausch-Schema: beim
    ''' Rückgängig wird die aktuelle Region als Wiederholen-Inhalt in denselben Patch getauscht).
    ''' Nach Budget-Verdrängung ist <see cref="IsAlive"/> False - die Pixel dieses Zugs sind dann
    ''' endgültig, das zugehörige Undo stellt nur noch die Regler/Objekte wieder her.</summary>
    Public NotInheritable Class WorkingImagePatch
        Friend Sub New(rect As SKRectI, pixels As SKBitmap)
            Me.Rect = rect
            Me.Pixels = pixels
        End Sub

        Public ReadOnly Property Rect As SKRectI
        Friend Property Pixels As SKBitmap
        Public Property IsAlive As Boolean = True

        Friend ReadOnly Property SizeBytes As Long
            Get
                Return If(Pixels Is Nothing, 0L, CLng(Pixels.RowBytes) * Pixels.Height)
            End Get
        End Property

        Friend Sub Kill()
            Pixels?.Dispose()
            Pixels = Nothing
            IsAlive = False
        End Sub
    End Class

    Public NotInheritable Class WorkingImageService
        Implements IDisposable

        ''' RAM-Deckel für Undo-Patches (Vorher-Ausschnitte). Beim Überschreiten fliegt der
        ''' ÄLTESTE Patch - dessen Zug ist dann pixel-final (bewusste Nutzerentscheidung
        ''' 2026-07-17: Undo-Tiefe gegen Speicher, wie die History-Grenze in Photoshop).
        ''' Feld statt Konstante: die Diagnose senkt den Deckel per Reflexion, um die
        ''' Verdrängung ohne Riesen-Bitmaps zu prüfen.
        Friend Shared _patchBudgetBytes As Long = 384L * 1024L * 1024L

        Private Shared ReadOnly PreviewSampling As New SKSamplingOptions(SKCubicResampler.Mitchell)

        Private ReadOnly _lock As New Object()
        Private _full As SKBitmap
        Private _preview As SKBitmap
        Private _version As Long
        ''' Zählt Init/Clear: ein Commit aus der Hintergrund-Queue, der ein ANDERES Arbeitsbild
        ''' meinte (Bildwechsel dazwischen), erkennt das am veränderten Stempel und verfällt.
        Private _initStamp As Long
        Private _hasBakedContent As Boolean
        Private _hasAlphaHoles As Boolean
        ''' Einfüge-Reihenfolge = Alter; vorne der älteste (Budget-Verdrängungskandidat).
        Private ReadOnly _patches As New List(Of WorkingImagePatch)()

        ''' <summary>Übernimmt das voll aufgelöste Bitmap (Besitz wechselt zum Service, auch im
        ''' Fehlerfall) und erzeugt die Vorschau-Ableitung mit exakt derselben Skalierung wie
        ''' ImageProcessor.CreatePreviewWorkingBitmap. Liefert das Vorschau-Bitmap (Besitz beim
        ''' Aufrufer, siehe Klassenkommentar) oder Nothing bei Fehler.</summary>
        Public Function Init(fullBitmap As SKBitmap, previewMaxDimension As Integer,
                             Optional hasBakedContent As Boolean = False,
                             Optional hasAlphaHoles As Boolean = False) As SKBitmap
            If fullBitmap Is Nothing Then Return Nothing
            ' JPEG & Co. dekodieren als AlphaType.Opaque. Der Radierer schreibt in so ein Bitmap
            ' zwar Alpha-Bytes (Anzeige und Composite-Render stimmen deshalb), aber der direkte
            ' PNG-Encode in EncodeFullPng verwirft sie wieder - die retouch.png der .fpx verlor
            ' so die Radierer-Transparenz (Nutzer-Befund 2026-07-17, nur bei JPG-Basisbild).
            ' Das Arbeitsbild muss jederzeit Löcher können: hier einmalig auf Premul normalisieren.
            If fullBitmap.AlphaType = SKAlphaType.Opaque Then
                Dim converted As SKBitmap = Nothing
                Try
                    converted = New SKBitmap(New SKImageInfo(fullBitmap.Width, fullBitmap.Height,
                                                             SKColorType.Bgra8888, SKAlphaType.Premul))
                    Using cv As New SKCanvas(converted)
                        cv.DrawBitmap(fullBitmap, 0, 0)
                    End Using
                Catch
                    converted?.Dispose()
                    converted = Nothing
                End Try
                If converted Is Nothing Then
                    fullBitmap.Dispose()
                    Return Nothing
                End If
                fullBitmap.Dispose()
                fullBitmap = converted
            End If
            Dim preview As SKBitmap = Nothing
            Try
                preview = ImageProcessor.CreatePreviewWorkingBitmap(fullBitmap, previewMaxDimension)
            Catch
                preview = Nothing
            End Try
            If preview Is Nothing Then
                fullBitmap.Dispose()
                Return Nothing
            End If
            ' Kleines Bild: CreatePreviewWorkingBitmap liefert die QUELLE selbst zurück - Voll und
            ' Vorschau wären dieselbe Instanz und würden doppelt disposed. Dann echte Kopie ziehen.
            If Object.ReferenceEquals(preview, fullBitmap) Then
                preview = fullBitmap.Copy()
                If preview Is Nothing Then
                    fullBitmap.Dispose()
                    Return Nothing
                End If
            End If
            SyncLock _lock
                For Each p In _patches
                    p.Kill()
                Next
                _patches.Clear()
                _full?.Dispose()
                _full = fullBitmap
                _preview = preview
                _version += 1
                _initStamp += 1
                ' hasBakedContent=True beim Laden einer .fpx, deren retouch.png bereits das fertige
                ' Arbeitsbild ist (Striche/Retusche eingebacken); hasAlphaHoles aus dem Rezept-Flag.
                _hasBakedContent = hasBakedContent
                _hasAlphaHoles = hasAlphaHoles
            End SyncLock
            Return preview
        End Function

        ''' <summary>Identität des aktuellen Arbeitsbilds (wechselt mit Init/Clear) - Commits aus
        ''' der Hintergrund-Queue prüfen damit, ob sie noch dasselbe Bild meinen.</summary>
        Public ReadOnly Property InitStamp As Long
            Get
                SyncLock _lock
                    Return _initStamp
                End SyncLock
            End Get
        End Property

        ''' <summary>PNG des vollen Arbeitsbilds (retouch.png der .fpx). Gleiches schnelles
        ''' Encoding wie die Pipeline (verlustfrei, Kompressionsstufe 60). Nothing ohne Init.</summary>
        Public Function EncodeFullPng() As IO.MemoryStream
            Dim clone = CloneFull()
            If clone Is Nothing Then Return Nothing
            Try
                Using image = SKImage.FromBitmap(clone)
                    Using data = image.Encode(SKEncodedImageFormat.Png, 60)
                        Dim ms As New IO.MemoryStream()
                        data.SaveTo(ms)
                        ms.Position = 0
                        Return ms
                    End Using
                End Using
            Finally
                clone.Dispose()
            End Try
        End Function

        Public ReadOnly Property IsInitialized As Boolean
            Get
                SyncLock _lock
                    Return _full IsNot Nothing
                End SyncLock
            End Get
        End Property

        ''' <summary>Zähler je Init (später je Commit) - wandert als WorkingImageVersion in den
        ''' Base-Cache-Key und verwirft damit veraltete Pipeline-Caches.</summary>
        Public ReadOnly Property Version As Long
            Get
                SyncLock _lock
                    Return _version
                End SyncLock
            End Get
        End Property

        ''' <summary>True, sobald Retusche/Striche/gerasterte Ebenen eingebacken wurden (ab Stufe D)
        ''' oder das Arbeitsbild aus einer .fpx-retouch.png geladen wurde.</summary>
        Public ReadOnly Property HasBakedContent As Boolean
            Get
                SyncLock _lock
                    Return _hasBakedContent
                End SyncLock
            End Get
        End Property

        ''' <summary>True, sobald der Radierer (oder transparentes Rastern) Alpha-Löcher ins
        ''' Arbeitsbild gestanzt hat - steuert Schachbrett und Transparenz beim Speichern.</summary>
        Public ReadOnly Property HasAlphaHoles As Boolean
            Get
                SyncLock _lock
                    Return _hasAlphaHoles
                End SyncLock
            End Get
        End Property

        Public ReadOnly Property FullWidth As Integer
            Get
                SyncLock _lock
                    Return If(_full?.Width, 0)
                End SyncLock
            End Get
        End Property

        Public ReadOnly Property FullHeight As Integer
            Get
                SyncLock _lock
                    Return If(_full?.Height, 0)
                End SyncLock
            End Get
        End Property

        ''' <summary>Kopie des vollen Arbeitsbilds (für Export/FPX-Speichern). Nothing ohne Init.</summary>
        Public Function CloneFull() As SKBitmap
            SyncLock _lock
                Return _full?.Copy()
            End SyncLock
        End Function

        ''' <summary>Herunterskalierte Kopie des Arbeitsbilds (z.B. Zoom-Detail-Quelle), gleiche
        ''' Skalierung wie die Vorschau-Ableitung. Nothing ohne Init.</summary>
        Public Function RenderDownscale(maxDimension As Integer) As SKBitmap
            SyncLock _lock
                If _full Is Nothing Then Return Nothing
                Dim scaled = ImageProcessor.CreatePreviewWorkingBitmap(_full, maxDimension)
                If scaled Is Nothing Then Return Nothing
                If Object.ReferenceEquals(scaled, _full) Then Return _full.Copy()
                Return scaled
            End SyncLock
        End Function

        ' ── Commits + Undo-Patches (Infrastruktur Stufe B, genutzt ab Stufe D) ──────────

        ''' <summary>Backt eine Region ins Arbeitsbild: sichert den VORHER-Ausschnitt als Patch,
        ''' ruft <paramref name="draw"/> mit dem VOLL-Bitmap auf (der Callback darf nur innerhalb
        ''' von <paramref name="rect"/> SCHREIBEN, lesen aus der Umgebung ist erlaubt - z.B.
        ''' Heal-Kandidatensuche), zieht die Vorschau-Region nach und erhöht die Version.
        ''' Läuft komplett unter dem Service-Lock - Commits sind dadurch strikt seriell, und
        ''' CloneFull (Export) wartet automatisch auf einen laufenden Commit.
        ''' Nothing bei leerer Region oder ohne Init.</summary>
        Public Function CommitRegion(rect As SKRectI, draw As Action(Of SKBitmap),
                                     Optional punchesAlpha As Boolean = False) As WorkingImagePatch
            If draw Is Nothing Then Return Nothing
            SyncLock _lock
                If _full Is Nothing Then Return Nothing
                Dim clamped = ClampToFullLocked(rect)
                If clamped.Width <= 0 OrElse clamped.Height <= 0 Then Return Nothing

                Dim before = ExtractRegionLocked(clamped)
                If before Is Nothing Then Return Nothing

                draw(_full)

                UpdatePreviewRegionLocked(clamped)
                _version += 1
                _hasBakedContent = True
                If punchesAlpha Then _hasAlphaHoles = True

                Dim patch As New WorkingImagePatch(clamped, before)
                _patches.Add(patch)
                EnforcePatchBudgetLocked()
                Return patch
            End SyncLock
        End Function

        ''' <summary>Rückgängig: tauscht den gespeicherten Regioninhalt mit dem aktuellen Stand
        ''' (der Patch hält danach die WIEDERHOLEN-Pixel). False, wenn der Patch dem Budget zum
        ''' Opfer fiel - der Zug ist dann pixel-final.</summary>
        Public Function RevertPatch(patch As WorkingImagePatch) As Boolean
            Return SwapPatch(patch)
        End Function

        ''' <summary>Wiederholen: identische Tauschoperation wie <see cref="RevertPatch"/> -
        ''' der Patch pendelt zwischen Vorher- und Nachher-Inhalt.</summary>
        Public Function ReapplyPatch(patch As WorkingImagePatch) As Boolean
            Return SwapPatch(patch)
        End Function

        Private Function SwapPatch(patch As WorkingImagePatch) As Boolean
            If patch Is Nothing Then Return False
            SyncLock _lock
                If _full Is Nothing OrElse Not patch.IsAlive OrElse patch.Pixels Is Nothing Then Return False
                Dim current = ExtractRegionLocked(patch.Rect)
                If current Is Nothing Then Return False
                Using canvas = New SKCanvas(_full)
                    ' Src-Blend ersetzt die Region exakt (inkl. Alpha) statt darüberzumischen.
                    Using paint = New SKPaint With {.BlendMode = SKBlendMode.Src}
                        canvas.DrawBitmap(patch.Pixels, patch.Rect.Left, patch.Rect.Top, paint)
                    End Using
                End Using
                patch.Pixels.Dispose()
                patch.Pixels = current
                UpdatePreviewRegionLocked(patch.Rect)
                _version += 1
                Return True
            End SyncLock
        End Function

        ''' <summary>Entsorgt einen Patch endgültig (z.B. wenn der Redo-Stapel nach einer neuen
        ''' Aktion geleert wird). Tote Patches sind ein No-Op.</summary>
        Public Sub DiscardPatch(patch As WorkingImagePatch)
            If patch Is Nothing Then Return
            SyncLock _lock
                _patches.Remove(patch)
                patch.Kill()
            End SyncLock
        End Sub

        Private Sub EnforcePatchBudgetLocked()
            Dim total As Long = 0
            For Each p In _patches
                total += p.SizeBytes
            Next
            While total > _patchBudgetBytes AndAlso _patches.Count > 1
                Dim oldest = _patches(0)
                _patches.RemoveAt(0)
                total -= oldest.SizeBytes
                oldest.Kill()
            End While
        End Sub

        Private Function ClampToFullLocked(rect As SKRectI) As SKRectI
            Return SKRectI.Intersect(rect, New SKRectI(0, 0, _full.Width, _full.Height))
        End Function

        ''' Kopiert einen Regionsausschnitt des VOLL-Bitmaps in ein eigenes Bitmap.
        Private Function ExtractRegionLocked(rect As SKRectI) As SKBitmap
            Dim region As New SKBitmap(rect.Width, rect.Height, _full.ColorType, _full.AlphaType)
            Using canvas = New SKCanvas(region)
                canvas.Clear(SKColors.Transparent)
                canvas.DrawBitmap(_full, rect, New SKRect(0, 0, rect.Width, rect.Height))
            End Using
            Return region
        End Function

        ''' <summary>Zieht die betroffene Region der VORSCHAU nach: derselbe Voll→Vorschau-Transform
        ''' wie bei Init (fixer Faktor, gleiche Abtastung), nur auf den Clip beschränkt - dadurch
        ''' pixelidentisch zum kompletten Neu-Downscale, ohne Nähte. SKImage.FromPixels wrappt die
        ''' Voll-Pixel ohne Kopie (FromBitmap würde ~200 MB je Commit kopieren).</summary>
        Private Sub UpdatePreviewRegionLocked(rect As SKRectI)
            If _preview Is Nothing OrElse _full Is Nothing Then Return
            If _preview.Width = _full.Width AndAlso _preview.Height = _full.Height Then
                ' Unskalierte Vorschau (kleines Bild): Region 1:1 ersetzen (Src-Blend, inkl. Alpha).
                Using canvas = New SKCanvas(_preview)
                    Using paint = New SKPaint With {.BlendMode = SKBlendMode.Src}
                        canvas.DrawBitmap(_full, rect, SKRect.Create(rect.Left, rect.Top, rect.Width, rect.Height), paint)
                    End Using
                End Using
                Return
            End If

            Dim scaleX = _preview.Width / CDbl(_full.Width)
            Dim scaleY = _preview.Height / CDbl(_full.Height)
            Dim previewRect = New SKRectI(
                Math.Max(0, CInt(Math.Floor(rect.Left * scaleX)) - 2),
                Math.Max(0, CInt(Math.Floor(rect.Top * scaleY)) - 2),
                Math.Min(_preview.Width, CInt(Math.Ceiling(rect.Right * scaleX)) + 2),
                Math.Min(_preview.Height, CInt(Math.Ceiling(rect.Bottom * scaleY)) + 2))
            If previewRect.Width <= 0 OrElse previewRect.Height <= 0 Then Return

            Using canvas = New SKCanvas(_preview)
                canvas.ClipRect(SKRect.Create(previewRect.Left, previewRect.Top, previewRect.Width, previewRect.Height))
                canvas.Clear(SKColors.Transparent)
                Using image = SKImage.FromPixels(_full.Info, _full.GetPixels(), _full.RowBytes)
                    Using paint = New SKPaint With {.IsAntialias = True}
                        canvas.DrawImage(image,
                                         New SKRect(0, 0, _full.Width, _full.Height),
                                         New SKRect(0, 0, _preview.Width, _preview.Height),
                                         PreviewSampling, paint)
                    End Using
                End Using
            End Using
        End Sub

        ''' <summary>Gibt das VOLL-Bitmap und alle Undo-Patches frei und vergisst die
        ''' Vorschau-Referenz (deren Besitz liegt beim EditorViewModel). Beim Bildwechsel und
        ''' Editor-Verlassen aufrufen.</summary>
        Public Sub Clear()
            SyncLock _lock
                For Each p In _patches
                    p.Kill()
                Next
                _patches.Clear()
                _full?.Dispose()
                _full = Nothing
                _preview = Nothing
                _initStamp += 1
                _hasBakedContent = False
                _hasAlphaHoles = False
            End SyncLock
        End Sub

        Public Sub Dispose() Implements IDisposable.Dispose
            Clear()
        End Sub

    End Class

End Namespace
