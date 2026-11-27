Imports System.Globalization
Imports System.IO
Imports System.Text
Imports SkiaSharp

Namespace Services

    ''' <summary>Schreibt die Anpassung eines Bildes als 3D-LUT (.cube), fuer den Dialog "Anpassung
    ''' speichern". Ein neutrales RGB-Gitter laeuft durch die verschmolzene Punktkette
    ''' (ImageProcessor.RenderPointOpLutGrid), das Ergebnis steht in der Reihenfolge des Formats in
    ''' der Datei: Rot laeuft am schnellsten, je Zeile drei Gleitkommazahlen mit sechs Stellen.
    '''
    ''' WAS EINE LUT TRAEGT: nur Punktoperationen, also Regler, bei denen der Ausgabewert eines
    ''' Bildpunkts allein von seinem Eingangswert abhaengt. Alles, was Nachbarn liest (Klarheit,
    ''' Schaerfe, Rauschen, Vignette, Korn ...), ist ausgegraut (<see cref="BlockReason"/>). Bei
    ''' einer RAW ebenso Belichtung und Weissabgleich: sie wirken in der RAW-Stufe vor der Kette,
    ''' am fertigen Gitter waeren sie etwas anderes. Das Filmnegativ misst seine Grundfarbe am Bild.
    '''
    ''' LICHTER, TIEFEN UND WEISS haengen an der Helligkeit des ganzen Bildes (ToneReference). Das
    ''' Gitter rechnet deshalb mit der des geoeffneten Fotos; die LUT trifft damit Bilder mit
    ''' aehnlicher Helligkeitsverteilung, und Lichter und Tiefen wirken in ihr wie auf einer glatten
    ''' Flaeche, ohne ihren oertlichen Anteil.
    '''
    ''' DER FARBMISCHER waehlt Band und Tor im Bild nach einer leicht geglaetteten Fuehrung
    ''' (MixerGuideSigma), in der LUT nach dem Bildpunkt selbst. Auf ruhigen Flaechen ist das
    ''' gleich; auf dem schmalen Saum einer Farbkante weicht die LUT ab, am staerksten bei einem
    ''' Farbton, den Kontrast und Saettigung danach noch spreizen.</summary>
    Public NotInheritable Class CubeLutWriter

        ''' Die waehlbaren Gitter: Punkte je Achse.
        Public Shared ReadOnly GridSizes As Integer() = {17, 33, 65}
        Public Const DefaultGridSize As Integer = 33
        Public Const FileExtension As String = ".cube"

        ''' Die Gruppen, deren Regler Nachbarn lesen und deshalb in keine LUT passen.
        Private Shared ReadOnly SpatialGroups As String() =
            {"details", "rauschen", "sharpen", "vignette", "grain", "tiltshift", "extensions", "frame"}

        Private Sub New()
        End Sub

        ''' <summary>Warum <paramref name="entry"/> in einer LUT nicht enthalten sein kann, als
        ''' Satz in der Sprache der Oberflaeche; Nothing, wenn er es kann.</summary>
        ''' <param name="isRaw">Kommt das Bild aus einer RAW-Datei? Dann wirken Belichtung und
        ''' Weissabgleich beim Entwickeln.</param>
        ''' <param name="recipe">Das Rezept des Bildes, fuer den Filter: der weiche Filter ist ein
        ''' Weichzeichner, die uebrigen sind Farbmatrizen.</param>
        Public Shared Function BlockReason(entry As AdjustmentEntry, isRaw As Boolean,
                                           Optional recipe As ImageAdjustments = Nothing) As String
            If entry Is Nothing Then Return Nothing
            If entry.IsFrame OrElse SpatialGroups.Contains(entry.GroupKey) Then
                Return LocalizationService.T("Hängt vom Ort im Bild ab und passt in keine LUT.")
            End If
            If entry.GroupKey = "film-negative" Then
                Return LocalizationService.T("Das Filmnegativ misst seine Grundfarbe am Bild und passt in keine LUT.")
            End If
            If isRaw AndAlso (entry.Key = "light.exposure" OrElse entry.Key = "color.white-balance") Then
                Return LocalizationService.T("Wirkt beim Entwickeln der RAW und passt in keine LUT.")
            End If
            If entry.GroupKey = "filter" AndAlso recipe IsNot Nothing AndAlso
               String.Equals(If(recipe.FilterPreset, "").Trim(), "weich", StringComparison.OrdinalIgnoreCase) Then
                Return LocalizationService.T("Hängt vom Ort im Bild ab und passt in keine LUT.")
            End If
            Return Nothing
        End Function

        ''' <summary>Das Rezept der LUT aus dem ganzen Rezept des Bildes (<paramref name="current"/>,
        ''' mit Herkunft des Quellbilds): die übertragbaren, gewählten Einträge.
        '''
        ''' KOMMT DAS BILD AUS DER RAW-STUFE, trägt das Rezept auch deren Herkunft: die Belichtung als
        ''' schon im Quellbild (ExposureInSource gleich Exposure, die Kette rechnet also keine), dazu
        ''' SourceFromRawStage. Die Kette des Editors richtet sich danach: in Tonfassung 1 nimmt sie
        ''' abgesenkte Lichter nach positiver Belichtung zurück (ImageAdjustments.ChainHighlights), und
        ''' der Verstärker der Feinstruktur fällt mit der Belichtung (BoostsLuminanceDetail). Ohne
        ''' diese Herkunft rechnete das Gitter mit dem vollen Regler und wich vom Editor ab.</summary>
        Public Shared Function ExtractRecipe(current As ImageAdjustments, entries As IEnumerable(Of AdjustmentEntry)) As ImageAdjustments
            If current Is Nothing Then Return New ImageAdjustments()
            Dim recipe = AdjustmentEntries.Extract(current.ExtractTransferableAdjustments(), entries)
            If current.BoostsLuminanceDetail() Then
                recipe.SourceFromRawStage = True
                recipe.Exposure = current.Exposure
                recipe.ExposureInSource = current.Exposure
            End If
            Return recipe
        End Function

        ''' <summary>Die Tabelle zu <paramref name="recipe"/>: <paramref name="gridSize"/> hoch drei
        ''' Punkte, je drei Werte. <paramref name="recipe"/> traegt nur, was in die LUT soll (der
        ''' Dialog zieht die gewaehlten und erlaubten Eintraege heraus).</summary>
        Friend Shared Function BuildTable(recipe As ImageAdjustments, gridSize As Integer,
                                          toneReference As ImageProcessor.ToneReference) As Single()
            Return ImageProcessor.RenderPointOpLutGrid(recipe, gridSize, toneReference)
        End Function

        ''' <summary>Wie oben, die Helligkeit gemessen an <paramref name="toneSource"/> (dem Bild, auf
        ''' das die Anpassung gehoert); ohne Bild misst die Kette am Gitter.</summary>
        Public Shared Function BuildTable(recipe As ImageAdjustments, gridSize As Integer,
                                          toneSource As SKBitmap) As Single()
            Dim reference As ImageProcessor.ToneReference = Nothing
            If toneSource IsNot Nothing Then reference = ImageProcessor.MeasureToneReference(toneSource)
            Return BuildTable(recipe, gridSize, reference)
        End Function

        ''' <summary>Der Text der .cube-Datei: Kopf mit Titel, Groesse und Eingangsbereich, danach
        ''' eine Zeile je Gitterpunkt.</summary>
        Public Shared Function Format(title As String, gridSize As Integer, table As Single()) As String
            If table Is Nothing OrElse table.Length <> gridSize * gridSize * gridSize * 3 Then
                Throw New ArgumentException("Die Tabelle passt nicht zur Groesse des Gitters.")
            End If
            Dim culture = CultureInfo.InvariantCulture
            Dim text As New StringBuilder(table.Length * 9 + 200)
            ' Ein Titel ohne Anfuehrungszeichen und Zeilenumbruch: beides beendete die Kopfzeile.
            Dim cleanTitle = If(title, "").Replace("""", "'").Replace(vbCr, " ").Replace(vbLf, " ").Trim()
            text.Append("# FerrumPix").Append(vbLf)
            text.Append("TITLE """).Append(cleanTitle).Append("""").Append(vbLf)
            text.Append("LUT_3D_SIZE ").Append(gridSize.ToString(culture)).Append(vbLf)
            text.Append("DOMAIN_MIN 0.0 0.0 0.0").Append(vbLf)
            text.Append("DOMAIN_MAX 1.0 1.0 1.0").Append(vbLf)
            text.Append(vbLf)
            For i = 0 To table.Length - 1 Step 3
                text.Append(Value(table(i), culture)).Append(" "c).
                     Append(Value(table(i + 1), culture)).Append(" "c).
                     Append(Value(table(i + 2), culture)).Append(vbLf)
            Next
            Return text.ToString()
        End Function

        Private Shared Function Value(v As Single, culture As CultureInfo) As String
            Dim clamped = If(Single.IsFinite(v), Math.Max(0.0F, Math.Min(1.0F, v)), 0.0F)
            Return clamped.ToString("F6", culture)
        End Function

        ''' <summary>Schreibt die Datei atomar (erst daneben, dann umbenannt) und vergisst einen
        ''' gemerkten Stand derselben Datei in der Kette.</summary>
        Public Shared Sub WriteFile(targetPath As String, title As String, gridSize As Integer, table As Single())
            JsonStoreService.WriteAtomicOrThrow(targetPath, Format(title, gridSize, table))
            ImageProcessor.ForgetCubeLut(targetPath)
        End Sub

    End Class

End Namespace
