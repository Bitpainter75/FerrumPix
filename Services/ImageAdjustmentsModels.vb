Imports System
Imports System.Buffers
Imports System.Collections.Generic
Imports System.ComponentModel
Imports System.IO
Imports System.Linq
Imports System.Runtime.CompilerServices
Imports System.Threading
Imports System.Threading.Tasks
Imports SkiaSharp
Imports Avalonia.Media.Imaging
Imports Avalonia.Platform
Imports System.Text.RegularExpressions
Imports System.Text.Json.Serialization
Imports System.Runtime.InteropServices
Imports QRCoder

' Das Rezept und seine Wertelisten: ImageAdjustments traegt jede Einstellung eines Bildes,
' BakedOperation vermerkt die Vorgaenge, die sich nicht als Regler ausdruecken lassen.
' Beides lag bis 2026-08-06 im Kopf von ImageProcessor.vb und teilt keinen Zustand mit dem
' Prozessor. Wer hier ein Feld ergaenzt, prueft BuildAdjustmentsFromFields und die
' Rezeptpruefungen der Diagnose mit.
' Verwandte Modelle: ImageAnnotationModels.vb (Objekte) und ImageMaskModels.vb (Masken).
Namespace Services

    Public Enum ResizeInterpolationMode
        Nearest
        Bilinear
        Bicubic
    End Enum

    Public Enum NoiseReductionMethod
        Gaussian
        Median
    End Enum

    ''' <summary>Wie eine dunkelnde Vignette auf die Pixel wirkt (angelehnt an Adobes
    ''' PostCropVignetteStyle). ColorPriority = 0 ist bewusst der erste Wert: es ist das bisherige
    ''' Verhalten (multiplikatives Abdunkeln, Farbton bleibt), damit ein fehlendes Feld in alten
    ''' .fpx-Projekten und der Enum-Standard exakt das ergeben, was vorher gerechnet wurde.</summary>
    Public Enum VignetteStyle
        ColorPriority
        HighlightPriority
        PaintOverlay
    End Enum



    ''' <summary>Ein Vorgang, der in die PIXEL gerechnet wurde und sich nicht als Regler ausdruecken
    ''' laesst: das Entrauschen mit Modell und das Objektentfernen.
    '''
    ''' WOZU: bei einer .fpx stecken solche Pixel im Buendel und ueberleben. Bei einem RAW oder PSD
    ''' liegt daneben nur das Rezept, und die Pixel entstehen bei jedem Oeffnen neu aus den
    ''' Sensordaten - ohne diesen Vermerk waere der Vorgang beim naechsten Oeffnen spurlos weg, und
    ''' zwar ohne Hinweis. Der Vermerk sagt, WAS getan wurde, damit der Editor es beim Oeffnen
    ''' anbieten kann.
    '''
    ''' DIE FALLE, DIE DEN ENTWURF ENTSCHEIDET: derselbe Vermerk bedeutet zweierlei. Bei einer .fpx
    ''' heisst er "steckt in diesen Pixeln", bei einem frisch entwickelten RAW "waere noch
    ''' anzuwenden". Deshalb steht neben der Liste das Feld
    ''' <see cref="ImageAdjustments.BakedOperationsApplied"/>, und es wird NICHT vom Editor gesetzt,
    ''' sondern von dem, der schreibt: wer Pixel mitschreibt, setzt es wahr, wer nur ein Rezept
    ''' schreibt, setzt es falsch. Sonst behauptet das Rezept frueher oder spaeter etwas, das im
    ''' Bild nicht steht.</summary>
    Public Class BakedOperation
        ''' <summary>"denoise" oder "objectremoval". Ein unbekannter Wert wird beim Nachziehen
        ''' uebersprungen - eine aeltere Programmfassung soll an einem neuen Vorgang nicht
        ''' scheitern, sondern ihn liegen lassen.</summary>
        Public Property Kind As String = ""

        ''' <summary>Nur beim Entrauschen: "quality" oder "fast". Welches Modell gerechnet hat,
        ''' gehoert dazu - die beiden liefern sichtbar Verschiedenes.</summary>
        Public Property DenoiseModel As String = ""

        ''' <summary>Nur beim Entrauschen: die Staerke, mit der die HELLIGKEIT entrauscht wurde,
        ''' 0 bis 100. Ohne sie kaeme beim Nachziehen etwas anderes heraus als beim ersten Mal.</summary>
        Public Property DenoiseStrength As Single = 100

        ''' <summary>Nur beim Objektentfernen: WAS entfernt wurde. Ohne die Maske ist der Vorgang
        ''' nicht wiederholbar - sie ist der ganze Auftrag. Es ist derselbe Maskentyp wie im
        ''' uebrigen Rezept, wird also mitgeschrieben und gelesen wie jede andere Maske.</summary>
        Public Property Mask As ImageMask = Nothing

        Public Function Clone() As BakedOperation
            Return New BakedOperation With {
                .Kind = Kind, .DenoiseModel = DenoiseModel, .DenoiseStrength = DenoiseStrength,
                .Mask = If(Mask Is Nothing, Nothing, Mask.Clone())
            }
        End Function

        Public Const KindDenoise As String = "denoise"
        Public Const KindObjectRemoval As String = "objectremoval"
    End Class

    Public Class ImageAdjustments

        ''' <summary>Die eingebauten Filter, in der Reihenfolge, in der sie im Editor stehen. "Keine" ist
        ''' der neutrale Eintrag. Einzige Quelle der Namen: sie sind gleichzeitig die Schlüssel, auf die
        ''' ImageProcessor.ApplyFilterPreset schaltet, und werden im Editor UND in der Stapelverarbeitung
        ''' der Galerie angeboten.</summary>
        Public Shared ReadOnly FilterPresetNames As String() = {
            "Keine", "S/W", "Warm", "Kühl", "Fade", "Kontrast", "Sepia", "Matt", "Cross", "Dramatisch",
            "Weich", "Noir", "Duoton", "Polaroid", "VHS", "Alt"
        }

        ''' <summary>Die Stärke, mit der ein frisch gewählter Filter startet. S/W und Sepia sind Umwandlungen -
        ''' halb angewendet ergeben sie nur ein blasses Bild, sie starten deshalb voll. Alle übrigen sind
        ''' Looks, die bei voller Stärke überzeichnen, und starten auf der Hälfte.</summary>
        Public Shared Function DefaultFilterStrength(preset As String) As Single
            Dim isConversion = String.Equals(preset, "S/W", StringComparison.OrdinalIgnoreCase) OrElse
                               String.Equals(preset, "Sepia", StringComparison.OrdinalIgnoreCase)
            Return If(isConversion, 100.0F, 50.0F)
        End Function

        Public Property SourceWidthPixels As Integer = 0
        Public Property SourceHeightPixels As Integer = 0
        Public Property RecipeCoordinateVersion As Integer = 2
        Public Property Exposure As Single = 0
        Public Property Brightness As Single = 0
        Public Property Contrast As Single = 0
        Public Property Saturation As Single = 0
        Public Property Highlights As Single = 0
        Public Property ShadowsLevel As Single = 0
        Public Property Whites As Single = 0
        Public Property Blacks As Single = 0
        Public Property Temperature As Single = 0
        Public Property Tint As Single = 0
        Public Property Sharpness As Single = 0
        ''' <summary>Radius der Unschärfemaske, 0-100. 0 = die bisherige feste 3×3-Maske (Radius ~1);
        ''' höher = größerer Wirkradius, schärft gröbere Strukturen. Bei 0 UND SharpenDetail 0 rechnet
        ''' ApplySharpness bitgenau wie zuvor.</summary>
        Public Property SharpenRadius As Single = 0
        ''' <summary>Detailanhebung der Unschärfemaske, 0-100. 0 = neutral; höher = die feinen
        ''' Hochfrequenzanteile werden stärker herausgearbeitet.</summary>
        Public Property SharpenDetail As Single = 0
        ''' <summary>Kantenmaskierung der Schärfung, 0-100 (Adobes „Maskieren"). 0 = die ganze Fläche wird
        ''' geschärft (bisheriges Verhalten); höher = die Schärfung zieht sich auf die Kanten zurück, glatte
        ''' Flächen wie Himmel und Haut bleiben ruhig. Bei 0 rechnet ApplySharpness bitgenau wie zuvor.</summary>
        Public Property SharpenMasking As Single = 0
        Public Property NoiseReduction As Single = 0
        ''' <summary>Kantenerhalt der (gaußschen) Rauschreduzierung, 0-100. 0 = reines Weichzeichnen wie
        ''' bisher; höher = an kontrastreichen Kanten wird das Original zurückgemischt, Details bleiben
        ''' stehen. Wirkt nur bei aktiver NoiseReduction.</summary>
        Public Property NoiseReductionDetail As Single = 0
        Public Property NoiseReductionMethod As NoiseReductionMethod = NoiseReductionMethod.Gaussian
        ''' Farb-Rauschreduzierung 0-100: glaettet NUR die Farbanteile (Chroma), die Helligkeit
        ''' bleibt unangetastet - Details bleiben stehen, Farbflecken verschwinden. Gerade bei der
        ''' echten RAW-Entwicklung sichtbar, wo die Kamera-Vorschau schon entrauscht war.
        Public Property ColorNoiseReduction As Single = 0

        ''' <summary>Mehrskalige Farbrauschminderung: 0 bis 100. Die dritte Stufe neben Rauschen und
        ''' Farbrauschen - sie holt das GROBFLECKIGE Farbrauschen, an das ein einstufiger Filter nur
        ''' mit einem Radius herankaeme, der alles andere gleich mitfrisst.</summary>
        Public Property FarbrauschGrob As Single = 0

        ''' <summary>Wie grosse Flecken noch erfasst werden: 0 bis 100.</summary>
        Public Property ColorNoiseCoarseScale As Single = 50
        ''' <summary>Farbrauschen HINZUFUEGEN 0-100 - die Plus-Seite desselben Reglers im Panel
        ''' („Farbrauschen" ist bipolar wie „Rauschen": minus entfernt, plus faerbt ein). Getrennt
        ''' gespeichert, damit <see cref="ColorNoiseReduction"/> weiter genau das bleibt, was
        ''' unter crs:ColorNoiseReduction abgelegt wird (0-100 Entfernung) - eine gemeinsame
        ''' Skala mit Vorzeichen haette den Preset-Import zweideutig gemacht.</summary>
        Public Property ColorNoiseAdd As Single = 0
        Public Property DustScratches As Single = 0
        Public Property Haze As Single = 0
        Public Property AddNoise As Single = 0
        Public Property [Structure] As Single = 0
        Public Property Glow As Single = 0
        Public Property Vibrance As Single = 0

        ''' KAMERAKALIBRIERUNG (Panel "Kalibrierung", crs:RedHue/RedSaturation/...).
        ''' Dreht und saettigt die drei PRIMAERFARBEN und ist damit das, was vielen Presets ihren
        ''' charakteristischen Farbstich gibt - ohne sie kam ein Preset strukturell unvollstaendig an.
        ''' Alle Werte -100..100. Naeherung: Farbton = Drehung der Primaerfarbe um die Grauachse
        ''' (bis +/-30 Grad), Saettigung = Abstand von der Grauachse. Adobes exakte Rechnung sitzt
        ''' im Kameraprofil und ist nicht oeffentlich; diese Form ist verbreitet und reproduzierbar.
        Public Property CalibrationRedHue As Single = 0
        Public Property CalibrationRedSaturation As Single = 0
        Public Property CalibrationGreenHue As Single = 0
        Public Property CalibrationGreenSaturation As Single = 0
        Public Property CalibrationBlueHue As Single = 0
        Public Property CalibrationBlueSaturation As Single = 0

        ''' Gruen-/Magenta-Verschiebung, die nur die TIEFEN faerbt (crs:ShadowTint).
        Public Property CalibrationShadowTint As Single = 0
        ' ── Objektivkorrektur, uebersteuert je Bild ─────────────────────────
        '
        ' Nothing heisst "wie in den Einstellungen vorgegeben". Bewusst dreiwertig und nicht einfach
        ' Boolean: waere die Vorgabe hier hineinkopiert, wuerde ein spaeter umgestellter Schalter
        ' alle bereits gespeicherten Rezepte nicht mehr erreichen - und man saehe der Datei nicht an,
        ' ob AUS eine Entscheidung war oder nur der damalige Standard.
        Public Property LensDistortion As Boolean? = Nothing
        Public Property LensTca As Boolean? = Nothing
        Public Property LensVignetting As Boolean? = Nothing

        ' Staerke je Korrektur in Prozent, 100 = wie kalibriert. Sie ist noetig, weil die Messwerte
        ' fuer ein OBJEKTIVMODELL gelten, nicht fuer das einzelne Exemplar - und weil sie an einer
        ' anderen Kamera aufgenommen wurden. Am eigenen Referenzfoto lag der Rotkanal bei doppelter
        ' Staerke naeher am Ziel als bei einfacher, der Blaukanal dagegen schlechter; ein fester
        ' Faktor kann das nicht abbilden.
        ''' <summary>Von Hand gewaehltes Objektiv, wenn die Aufnahmedaten KEINES nennen. Dann gibt
        ''' es keinen Objektivnamen, an dem sich eine dauerhafte Zuordnung festmachen liesse - also
        ''' gehoert die Wahl ins Rezept dieses einen Bildes. Nennt das EXIF ein Objektiv, bleibt es
        ''' bei der Zuordnung ueber den Namen, die dann fuer den ganzen Bestand gilt.</summary>
        Public Property LensModel As String = ""

        Public Property LensDistortionAmount As Single = 100
        Public Property LensTcaAmount As Single = 100
        Public Property LensVignettingAmount As Single = 100

        Public Property Vignette As Single = 0
        Public Property VignetteTransition As Single = 55
        Public Property VignetteRoundness As Single = 0
        Public Property VignetteFeather As Single = 70
        Public Property VignetteCenterX As Single = 50
        Public Property VignetteCenterY As Single = 50
        ''' <summary>Stil, mit dem eine dunkelnde Vignette wirkt. Standard = ColorPriority = bisheriges
        ''' Verhalten (siehe <see cref="Services.VignetteStyle"/>).</summary>
        Public Property VignetteStyle As VignetteStyle = VignetteStyle.ColorPriority
        Public Property Grain As Single = 0
        ''' <summary>Körnungsgröße, 0-100. 0 = feinstes Korn (1 px, bisheriges Verhalten); höher =
        ''' gröberes Korn (das Rauschen wird zellenweise über größere Blöcke konstant gehalten).
        ''' Bei 0 UND GrainFrequency 0 rechnet ApplyGrain bitgenau wie zuvor.</summary>
        Public Property GrainSize As Single = 0
        ''' <summary>Körnungsfrequenz/Unregelmäßigkeit, 0-100. 0 = gleichmäßiges Korn; höher = eine
        ''' feine zweite Lage wird eingemischt, das Korn wirkt unruhiger.</summary>
        Public Property GrainFrequency As Single = 0
        Public Property Clarity As Single = 0

        ''' <summary>Gescanntes Filmnegativ in ein Positiv umkehren.</summary>
        Public Property NegativeEnabled As Boolean = False
        ''' <summary>Schwarzweiß-Negativ: ein gemeinsamer Basiswert für alle drei Kanäle. Die kanalweise
        ''' Normalisierung würde bei einem Graustufenscan sonst das Kanalrauschen zu einem Farbstich
        ''' aufziehen - es gibt hier keine Maske, die sie herausrechnen müsste.</summary>
        Public Property NegativeMonochrome As Boolean = False
        ''' <summary>Farbe des unbelichteten Filmträgers ("orange Maske") = die hellste Stelle des Scans.
        ''' Leer: wird beim Verarbeiten aus dem Bild geschätzt (siehe AnalyzeFilmNegative).</summary>
        Public Property NegativeBaseColor As String = ""
        ''' <summary>Dichteste (= dunkelste) Stelle des Negativs, entspricht dem hellsten Motivdetail.
        ''' Leer: wird geschätzt.</summary>
        Public Property NegativeDensityColor As String = ""
        ''' <summary>Gradation der Dichtekurve, -100..100 (0 = neutral), wirkt als Gamma 2^(v/100).</summary>
        Public Property NegativeGamma As Single = 0

        Public Property CurveRgbPoints As String = "0,0;255,255"
        Public Property CurveRedPoints As String = "0,0;255,255"
        Public Property CurveGreenPoints As String = "0,0;255,255"
        Public Property CurveBluePoints As String = "0,0;255,255"
        Public Property CurveLuminancePoints As String = "0,0;255,255"
        Public Property RedHue As Single = 0
        Public Property RedSaturation As Single = 0
        Public Property RedLuminance As Single = 0
        Public Property OrangeHue As Single = 0
        Public Property OrangeSaturation As Single = 0
        Public Property OrangeLuminance As Single = 0
        Public Property YellowHue As Single = 0
        Public Property YellowSaturation As Single = 0
        Public Property YellowLuminance As Single = 0
        Public Property GreenHue As Single = 0
        Public Property GreenSaturation As Single = 0
        Public Property GreenLuminance As Single = 0
        Public Property AquaHue As Single = 0
        Public Property AquaSaturation As Single = 0
        Public Property AquaLuminance As Single = 0
        Public Property BlueHue As Single = 0
        Public Property BlueSaturation As Single = 0
        Public Property BlueLuminance As Single = 0
        Public Property PurpleHue As Single = 0
        Public Property PurpleSaturation As Single = 0
        Public Property PurpleLuminance As Single = 0
        Public Property MagentaHue As Single = 0
        Public Property MagentaSaturation As Single = 0
        Public Property MagentaLuminance As Single = 0
        ''' <summary>Farbgradierung: vier Zonen (Schatten/Mitten/Lichter/Global) mit je Farbton (0-360),
        ''' Sättigung (0-100) und Luminanz (±100). Die Schatten- und Lichter-Felder hießen bis
        ''' SplitToning*: Split-Toning ist die Zweizonen-Variante desselben Werkzeugs, und Adobe hat es
        ''' ab den neueren Prozessversionen genauso in die Farbgradierung überführt (crs:SplitToning* wird beim Import
        ''' weiterhin gelesen, siehe XmpPresetService).</summary>
        Public Property ColorGradeShadowHue As Single = 0
        Public Property ColorGradeShadowSaturation As Single = 0
        Public Property ColorGradeShadowLuminance As Single = 0
        Public Property ColorGradeMidtoneHue As Single = 0
        Public Property ColorGradeMidtoneSaturation As Single = 0
        Public Property ColorGradeMidtoneLuminance As Single = 0
        Public Property ColorGradeHighlightHue As Single = 0
        Public Property ColorGradeHighlightSaturation As Single = 0
        Public Property ColorGradeHighlightLuminance As Single = 0
        Public Property ColorGradeGlobalHue As Single = 0
        Public Property ColorGradeGlobalSaturation As Single = 0
        Public Property ColorGradeGlobalLuminance As Single = 0
        ''' <summary>Verschiebt die Grenze zwischen Schatten- und Lichterzone (±100).</summary>
        Public Property ColorGradeBalance As Single = 0
        ''' <summary>Wie weich die Zonen ineinander übergehen (0-100, 50 = neutral). Wirkt als Exponent
        ''' auf die Zonengewichte: kleiner = die Tönungen bleiben stärker in ihrer Zone, größer = sie
        ''' greifen weiter ineinander. Bei 50 rechnet die Kette exakt wie das frühere Split-Toning.</summary>
        Public Property ColorGradeBlending As Single = 50
        Public Property RotationDegrees As Integer = 0
        ' ── Verzerren (perspektivisch) ──────────────────────────────────────
        '
        ' Alle vier laufen -100..100 und sind bei 0 wirkungslos. Sie gehoeren zur GEOMETRIE, nicht
        ' zu den Reglern: sie veraendern, wo ein Bildpunkt landet, und stehen deshalb unten auch in
        ' der Liste der strukturellen Felder.
        Public Property PerspectiveHorizontal As Single = 0
        Public Property PerspectiveVertical As Single = 0
        Public Property PerspectiveAspect As Single = 0
        Public Property PerspectiveScale As Single = 0

        ' Freie Ecken. Acht Werte in PROZENT der Bildbreite bzw. -hoehe, im Uhrzeigersinn ab links
        ' oben, bei 0 wirkungslos. Sie kommen ZUSAETZLICH zu den vier Reglern: die Regler kippen um
        ' die beiden Achsen (symmetrisch, das braucht man fuer stuerzende Linien), die Ecken
        ' erlauben jede Lage, die eine Homographie hergibt.
        '
        ' Warum beides und nicht nur die Ecken: die vier Regler sind in bestehenden Rezepten
        ' gespeichert und behalten hier ihre Bedeutung unveraendert. Und sie sind der bequemere
        ' Griff fuer den haeufigsten Fall - eine Kante geradeziehen, ohne vier Ecken einzeln zu
        ' treffen. Zwei Wege zur selben Matrix, aber nicht zwei Kopien derselben Formel: die Regler
        ' verschieben die Ecken, danach rechnet EINE Stelle weiter.
        Public Property PerspectiveCorner0X As Single = 0
        Public Property PerspectiveCorner0Y As Single = 0
        Public Property PerspectiveCorner1X As Single = 0
        Public Property PerspectiveCorner1Y As Single = 0
        Public Property PerspectiveCorner2X As Single = 0
        Public Property PerspectiveCorner2Y As Single = 0
        Public Property PerspectiveCorner3X As Single = 0
        Public Property PerspectiveCorner3Y As Single = 0

        ' ── Verzerren (Raster, Linien, Verformen) ───────────────────────────
        '
        ' Was keine Matrix ist, steht als KNOTENRASTER hier: je Rasterpunkt, wohin er wandert, in
        ' Prozent des unbeschnittenen Bildes. Gitter-, Linien- und Hüllenverformung erzeugen alle
        ' dieselbe Form - genau wie bei einem Objekt, und aus demselben Grund: nur ein Raster laesst
        ' sich verketten, ohne fuer jede Paarung von Arten zu ueberlegen, was ihre Verkettung ist.
        '
        ' Frueher wurde stattdessen in die Pixel GEBACKEN. Das kostete die Bearbeitbarkeit (zurueck
        ' ging es nur innerhalb der Sitzung) und war bei RAW und PSD sogar ganz verloren: neben
        ' diesen Dateien liegt nur das Rezept, kein einziges Pixel. Als Rezeptwert laeuft die
        ' Verzerrung jetzt in derselben Geometriestufe wie Beschnitt, Drehung und Perspektive - im
        ' Bildweg UND im Maskenweg, Masken wandern also von selbst mit.
        Public Property ImageWarp As ObjectWarp

        Public Property StraightenDegrees As Single = 0
        Public Property StraightenExpandCanvas As Boolean = False
        Public Property FlipHorizontal As Boolean = False
        Public Property FlipVertical As Boolean = False
        Public Property CropLeftPercent As Single = 0
        Public Property CropTopPercent As Single = 0
        Public Property CropRightPercent As Single = 0
        Public Property CropBottomPercent As Single = 0
        Public Property ResizeWidth As Integer = 0
        Public Property ResizeHeight As Integer = 0
        Public Property LockResizeAspect As Boolean = True

        ''' <summary>Zielmasse als KASTEN lesen statt als exakte Groesse (Stapel/Export): das Bild
        ''' wird verhaeltniswahrend eingepasst, ein einzelner Wert begrenzt die laengste Kante.
        ''' Der EDITOR setzt das bewusst NICHT: dort haengen beide Felder am Arbeitsmass VOR dem
        ''' Ausrichten - mit Einpassen kaeme bei erweiterter Leinwand etwas anderes heraus, als
        ''' das Panel anzeigt (4000x3000 + 5 Grad ergab 1908x1500 statt
        ''' 2000x1500). Ausserdem ist "laengste Kante" beim Einzelbild ueberraschend.</summary>
        Public Property ResizeFitInsideBox As Boolean = False

        ''' <summary>Prozentuale Zielgroesse (&gt;0) statt fester Masse. Wird auf dem TATSAECHLICH
        ''' dekodierten Bild gerechnet - die Masse vorab aus der Datei zu schaetzen ging fuer
        ''' RAW/PSD/.fpx schief (SKCodec kennt sie nicht) und bei EXIF-gedrehten JPEGs lag es um
        ''' die Drehung daneben.</summary>
        Public Property ResizeScalePercent As Double = 0

        ''' <summary>Kein Hochskalieren: ist das Bild bereits kleiner als die Zielmasse, bleibt es
        ''' unveraendert (wie "Don't Enlarge" in Export-Dialogen bzw. das Suffix "&gt;" bei
        ''' ImageMagick). Verhindert vorgetaeuschte Aufloesung in gemischten Stapeln.</summary>
        Public Property NoResizeUpscale As Boolean = False
        Public Property ResizeInterpolation As ResizeInterpolationMode = ResizeInterpolationMode.Bilinear

        ''' <summary>Hochskalieren mit einem gelernten Modell - der Schluessel des Modells, leer heisst
        ''' nichts tun. Siehe <c>UpscaleModelService</c>.
        '''
        ''' Dieses Feld wirkt AUSSCHLIESSLICH im Speicherweg und NICHT in der Vorschaukette. Das ist
        ''' kein Versehen: ein Durchlauf kostet Sekunden bis Minuten, und in der Vorschau liefe er
        ''' bei jeder Reglerbewegung erneut. Es steht hier bei den Groessenfeldern, weil es zur
        ''' Groesse gehoert - angewandt wird es aber wie ein eingebackener Vorgang, VOR der
        ''' Reglerkette. So kann danach noch gewoehnlich auf ein Zielmass verkleinert werden, und
        ''' das ist die richtige Reihenfolge: vom Grossen herunter ist ein Mitteln und verliert
        ''' nichts, umgekehrt wird geraten.</summary>
        Public Property UpscaleModel As String = ""

        Public Property CanvasWidth As Integer = 0
        Public Property CanvasHeight As Integer = 0
        Public Property LockCanvasAspect As Boolean = True
        Public Property CanvasAnchor As String = "Center"
        ''' <summary>Die Hintergrundfarbe des DOKUMENTS, als "#AARRGGBB". Voellig durchsichtig ist
        ''' der Ausgangszustand und aendert nichts.
        '''
        ''' Der Name stammt aus der Zeit, als sie nur die erweiterte Leinwand fuellte. Sie fuellt
        ''' jetzt ALLES, was sonst durchsichtig bliebe: erweiterte Leinwand, leere Ecken von
        ''' Begradigen und Verzerren, weggeradierte Stellen. Deshalb sitzt sie am ENDE der
        ''' Geometriekette - jede Stufe, die Loecher hinterlaesst, waere sonst eine eigene
        ''' Einstellung, und man muesste an jede einzeln denken. Der Feldname bleibt, damit
        ''' gespeicherte Rezepte weiter gelesen werden.</summary>
        Public Property CanvasBackgroundColor As String = "#00000000"
        Public Property FilterPreset As String = "Keine"
        Public Property FilterStrength As Single = 100
        Public Property LutPath As String = ""
        Public Property LutStrength As Single = 100
        Public Property RetouchSpots As New System.Collections.Generic.List(Of RetouchSpot)()
        Public Property Annotations As New System.Collections.Generic.List(Of ImageAnnotation)()
        ''' <summary>Objekt-Gruppen (siehe <see cref="AnnotationGroup"/>). Verwaist eine Gruppe (kein
        ''' Mitglied mehr), wird sie beim nächsten Aufräumen entfernt.</summary>
        Public Property AnnotationGroups As New System.Collections.Generic.List(Of AnnotationGroup)()
        Public Property RasterPaintStrokes As New System.Collections.Generic.List(Of PixelPaintStroke)()
        ''' <summary>Masken werden einmal gespeichert und können von mehreren lokalen Korrekturen benutzt werden.</summary>
        Public Property Masks As New System.Collections.Generic.List(Of ImageMask)()
        Public Property MaskedAdjustmentLayers As New System.Collections.Generic.List(Of MaskedAdjustmentLayer)()
        ''' <summary>Versionszähler des ARBEITSBILDS: geht in den Base-Cache-Key
        ''' ein und verwirft Pipeline-Caches nach jedem eingebackenen Commit. Kein Bestandteil des
        ''' Rezepts im inhaltlichen Sinn (reiner Cache-Stempel), schadet aber serialisiert nicht.</summary>
        Public Property WorkingImageVersion As Long = 0
        ''' <summary>True, wenn der Radierer (oder transparentes Rastern) Alpha-Löcher ins
        ''' Arbeitsbild gestanzt hat - im .fpx-Rezept persistiert, damit Schachbrett und
        ''' Transparenz-Verhalten das Wiederöffnen überleben.</summary>
        Public Property WorkingImageHasTransparency As Boolean = False

        ''' <summary>Vorgänge, die in die PIXEL gerechnet wurden: Entrauschen mit Modell und
        ''' Objektentfernen (siehe <see cref="BakedOperation"/>). In der REIHENFOLGE ihrer Anwendung -
        ''' beim Nachziehen wird sie eingehalten, sonst käme etwas anderes heraus.</summary>
        Public Property BakedOperations As New System.Collections.Generic.List(Of BakedOperation)()

        ''' <summary>Stecken die Vorgänge aus <see cref="BakedOperations"/> bereits in den Pixeln, zu
        ''' denen dieses Rezept gehört?
        '''
        ''' True bei einer .fpx: das Bündel trägt das Arbeitsbild mit allem Eingebackenen.
        ''' False bei einer .fpxmp: daneben liegt nur das Rezept, die Pixel entstehen beim Öffnen neu
        ''' aus den Sensordaten, und die Vorgänge sind noch anzuwenden.
        '''
        ''' Gesetzt wird das NICHT vom Editor, sondern von dem, der schreibt (RawSidecarService bzw.
        ''' FpxService). Der Editor kennt beim Setzen der Vorgänge noch gar nicht, wohin gespeichert
        ''' wird.</summary>
        Public Property BakedOperationsApplied As Boolean = False
        ''' <summary>Persistenter Render-Skopus der gespeicherten Auswahlmaske. Im Gegensatz zu
        ''' HasActiveSelection ist dies Bildrezept und kein transient markierter UI-Zustand.</summary>
        Public Property SelectionScopeEnabled As Boolean = False
        ''' <summary>True nur solange die Auswahl im Editor aktiv bearbeitet wird. FPX speichert
        ''' diesen transienten UI-Zustand bewusst als False.</summary>
        Public Property HasActiveSelection As Boolean = False

        ''' <summary>Art der aktiven Auswahl: True = MASKE (rotes Overlay), False = AUSWAHL
        ''' (Laufameisen). Gehoert in den Rueckgaengig-Zustand, weil das Wiederherstellen sie sonst
        ''' raten muesste - und pauschal auf "Auswahl" zurueckfiel. Nach einem Rueckgaengig zeigte
        ''' eine Maske dann Laufameisen statt des roten Overlays.
        '''
        ''' In ein DOKUMENT gehoert sie so wenig wie HasActiveSelection: beim Oeffnen holte sie ein
        ''' rotes Overlay herauf, das zu keiner markierten Ebene mehr gehoerte (siehe
        ''' FpxService.StripTransientSelectionState). Sie ist deshalb auch keine Pixel-Anpassung und
        ''' reist weder mit einer Vorlage noch mit einem Objekt mit.</summary>
        Public Property ActiveSelectionIsMask As Boolean = False
        Public Property SelectionXPercent As Double = 0
        Public Property SelectionYPercent As Double = 0
        Public Property SelectionWidthPercent As Double = 0
        Public Property SelectionHeightPercent As Double = 0
        Public Property SelectionShapeMode As String = "Rectangle"
        Public Property SelectionShapePointsX As Double() = Nothing
        Public Property SelectionShapePointsY As Double() = Nothing
        Public Property SelectionMaskLeft As Integer = 0
        Public Property SelectionMaskTop As Integer = 0
        Public Property SelectionMaskRight As Integer = 0
        Public Property SelectionMaskBottom As Integer = 0
        Public Property SelectionMaskPngBase64 As String = ""

        ''' <summary>Weiche Kante der Auswahl in BILDpixeln. Die gespeicherte Maske bleibt hart und
        ''' pixelgenau - weich wird die Kante erst bei der Verwendung (Anpassungs-Skopus, Kopieren, Füllen).
        ''' So lässt sich der Wert jederzeit ändern, ohne die Auswahl neu zu ziehen.</summary>
        Public Property SelectionFeatherPixels As Single = 0

        ''' <summary>True = die Auswahlmaske wurde mit dem Masken-Pinsel gemalt und trägt ihre weiche
        ''' Kante bereits IN den Maskenwerten (weiche Alpha8-Stempel). Dann darf der lazy globale
        ''' Feather (SelectionFeatherPixels) NICHT erneut weichzeichnen, sonst wäre die Kante doppelt
        ''' weich. Für harte geometrische Auswahlen (Rechteck/Ellipse/Lasso/Zauberstab) bleibt es False,
        ''' dort wirkt der globale Feather wie bisher.</summary>
        Public Property SelectionMaskSoftBaked As Boolean = False

        ''' <summary>True = die gemeinsame globale Anpassungsebene (Anpassen, Farbe, Details,
        ''' Effekte und Filter) wird beim Rendern übersprungen. Geometrie, lokale maskierte
        ''' Korrekturen, Retusche und Objekte bleiben davon unberührt.</summary>
        Public Property GlobalAdjustmentsHidden As Boolean = False

        ''' <summary>True = die Hintergrund-Ebene (das Basisbild) wird beim Zusammensetzen ausgeblendet; es
        ''' bleiben nur die Objekt-Ebenen auf transparentem Grund. Strukturell, keine Pixel-Anpassung, und
        ''' gehört NICHT zu den Eigenschaften, die ein einzelnes Objekt mitträgt (siehe StructuralPropertyNames).</summary>
        Public Property BackgroundHidden As Boolean = False

        ''' <summary>True = die eingebackene Pixel-Ebene wird beim Zusammensetzen uebersprungen, der Render
        ''' laeuft also auf dem UNGEBACKENEN Basisbild. Das ist die Ebene "Retusche und Pinsel" im
        ''' Ebenen-Panel: Retusche, Striche und gerasterte Ebenen liegen alle in EINEM Arbeitsbild
        ''' (siehe WorkingImageService) und lassen sich deshalb nur gemeinsam ausblenden. Strukturell,
        ''' keine Pixel-Anpassung (siehe StructuralPropertyNames).</summary>
        Public Property PixelLayerHidden As Boolean = False

        Public Shared Function IsIdentityCurve(pointsCsv As String) As Boolean
            Return String.IsNullOrWhiteSpace(pointsCsv) OrElse String.Equals(pointsCsv.Trim(), "0,0;255,255", StringComparison.Ordinal)
        End Function

        Public Function HasHslChanges() As Boolean
            Return RedHue <> 0 OrElse RedSaturation <> 0 OrElse RedLuminance <> 0 OrElse
                   OrangeHue <> 0 OrElse OrangeSaturation <> 0 OrElse OrangeLuminance <> 0 OrElse
                   YellowHue <> 0 OrElse YellowSaturation <> 0 OrElse YellowLuminance <> 0 OrElse
                   GreenHue <> 0 OrElse GreenSaturation <> 0 OrElse GreenLuminance <> 0 OrElse
                   AquaHue <> 0 OrElse AquaSaturation <> 0 OrElse AquaLuminance <> 0 OrElse
                   BlueHue <> 0 OrElse BlueSaturation <> 0 OrElse BlueLuminance <> 0 OrElse
                   PurpleHue <> 0 OrElse PurpleSaturation <> 0 OrElse PurpleLuminance <> 0 OrElse
                   MagentaHue <> 0 OrElse MagentaSaturation <> 0 OrElse MagentaLuminance <> 0
        End Function

        ''' <summary>Felder, die die STRUKTUR beschreiben: Geometrie, Objekte, Retusche, Auswahl, Quellmaße.
        ''' Alles andere sind Pixel-Anpassungen (Belichtung, Farbe, Details, Effekte, Filter, Kurven, HSL …)
        ''' - und genau die können auch auf ein einzelnes OBJEKT wirken statt aufs Bild (siehe
        ''' <see cref="ImageAnnotation.Adjustments"/>).
        '''
        ''' „Rahmen" steht bewusst hier: er zieht seinen Rand an den BILDkanten. Ein Rahmen um ein Objekt
        ''' wäre etwas anderes und gibt es noch nicht - er bliebe sonst als Rahmen ums ganze Bild stehen,
        ''' während man ein Objekt bearbeitet.</summary>
        Private Shared ReadOnly StructuralPropertyNames As New HashSet(Of String)(StringComparer.Ordinal) From {
            "SourceWidthPixels", "SourceHeightPixels", "RecipeCoordinateVersion",
            "WorkingImageVersion", "WorkingImageHasTransparency",
            "BakedOperations", "BakedOperationsApplied",
            "RotationDegrees", "StraightenDegrees", "StraightenExpandCanvas", "FlipHorizontal", "FlipVertical",
            "PerspectiveHorizontal", "PerspectiveVertical", "PerspectiveAspect", "PerspectiveScale",
            "PerspectiveCorner0X", "PerspectiveCorner0Y", "PerspectiveCorner1X", "PerspectiveCorner1Y",
            "PerspectiveCorner2X", "PerspectiveCorner2Y", "PerspectiveCorner3X", "PerspectiveCorner3Y",
            "ImageWarp",
            "CropLeftPercent", "CropTopPercent", "CropRightPercent", "CropBottomPercent",
            "ResizeWidth", "ResizeHeight", "LockResizeAspect", "ResizeFitInsideBox", "ResizeScalePercent", "NoResizeUpscale", "ResizeInterpolation",
            "UpscaleModel",
            "CanvasWidth", "CanvasHeight", "LockCanvasAspect", "CanvasAnchor", "CanvasBackgroundColor",
            "RetouchSpots", "Annotations", "AnnotationGroups", "RasterPaintStrokes", "Masks", "MaskedAdjustmentLayers",
            "SelectionScopeEnabled", "HasActiveSelection", "ActiveSelectionIsMask",
            "SelectionXPercent", "SelectionYPercent", "SelectionWidthPercent",
            "SelectionHeightPercent", "SelectionShapeMode", "SelectionShapePointsX", "SelectionShapePointsY",
            "SelectionMaskLeft", "SelectionMaskTop", "SelectionMaskRight", "SelectionMaskBottom",
            "SelectionMaskPngBase64", "SelectionFeatherPixels", "SelectionMaskSoftBaked", "GlobalAdjustmentsHidden", "BackgroundHidden", "PixelLayerHidden"
        }

        Private Shared _pixelProperties As Reflection.PropertyInfo() = Nothing
        Private Shared ReadOnly _pixelPropertiesLock As New Object()

        ''' <summary>Alle Pixel-Anpassungen, per Reflexion aus der Klasse selbst gewonnen: eine neue
        ''' Einstellung ist damit automatisch dabei und kann nicht vergessen werden.</summary>
        Public Shared Function PixelAdjustmentProperties() As Reflection.PropertyInfo()
            SyncLock _pixelPropertiesLock
                If _pixelProperties Is Nothing Then
                    _pixelProperties = GetType(ImageAdjustments).
                        GetProperties(Reflection.BindingFlags.Public Or Reflection.BindingFlags.Instance).
                        Where(Function(p) p.CanRead AndAlso p.CanWrite AndAlso Not StructuralPropertyNames.Contains(p.Name)).
                        ToArray()
                End If
                Return _pixelProperties
            End SyncLock
        End Function

        ''' <summary>Übernimmt alle Pixel-Anpassungen aus <paramref name="other"/>; Struktur bleibt unberührt.</summary>
        Public Sub CopyPixelAdjustmentsFrom(other As ImageAdjustments)
            If other Is Nothing Then Return
            For Each p In PixelAdjustmentProperties()
                p.SetValue(Me, p.GetValue(other))
            Next
        End Sub

        ''' <summary>Legt die Pixel-Anpassungen aus <paramref name="look"/> UEBER die eigenen -
        ''' aber nur die, die vom neutralen Standard abweichen. Alles, was der Look nicht anfasst,
        ''' bleibt so, wie es hier steht.
        '''
        ''' Gebraucht von den Stapelfunktionen: liegt neben einer RAW-Datei ein .fpxmp-Rezept, ist
        ''' DAS die Grundlage, und der Stapelschritt (ein Filter, ein Preset) kommt oben drauf.
        ''' Wuerde stattdessen der ganze Look kopiert, loeschte er mit seinen Nullwerten die
        ''' Bearbeitung des Nutzers - eine entwickelte RAW kaeme flach aus dem Stapel zurueck.
        '''
        ''' Die Kehrseite, bewusst in Kauf genommen: ein Look, der einen Regler ABSICHTLICH auf
        ''' null stellt, setzt sich gegen ein Rezept mit Wert dort nicht durch. Ein Look ist eine
        ''' Zutat, kein Zuruecksetzen - dafuer gibt es den Zuruecksetzen-Pfeil.</summary>
        Public Sub MergeNonDefaultPixelAdjustmentsFrom(look As ImageAdjustments)
            If look Is Nothing Then Return
            Dim neutral = New ImageAdjustments()
            For Each p In PixelAdjustmentProperties()
                Dim value = p.GetValue(look)
                Dim standard = p.GetValue(neutral)
                If Not Equals(value, standard) Then p.SetValue(Me, value)
            Next
        End Sub

        ''' <summary>Nur die Pixel-Anpassungen als eigenes Objekt - das ist der Satz, den ein Objekt mitträgt.</summary>
        Public Function ExtractPixelAdjustments() As ImageAdjustments
            Dim result = New ImageAdjustments()
            result.CopyPixelAdjustmentsFrom(Me)
            Return result
        End Function

        ''' <summary>True, sobald mindestens eine Korrekturebene IN den Objektstapel einsortiert ist.
        ''' Solche Ebenen wirken auf das Komposit und lassen sich deshalb nicht aus dem Basis-Cache
        ''' bedienen - der Editor muss dann voll rendern statt nur eine Region zu flicken.</summary>
        Public Function HasStackedCorrectionLayers() As Boolean
            If MaskedAdjustmentLayers Is Nothing Then Return False
            For Each l In MaskedAdjustmentLayers
                If l IsNot Nothing AndAlso Not String.IsNullOrEmpty(l.StackAboveAnnotationId) Then Return True
            Next
            Return False
        End Function

        ''' <summary>Findet die Gruppe eines Objekts (Nothing, wenn es keiner angehört).</summary>
        Public Function FindAnnotationGroup(annotation As ImageAnnotation) As AnnotationGroup
            If annotation Is Nothing OrElse String.IsNullOrEmpty(annotation.GroupId) Then Return Nothing
            If AnnotationGroups Is Nothing Then Return Nothing
            For Each g In AnnotationGroups
                If g IsNot Nothing AndAlso String.Equals(g.Id, annotation.GroupId, StringComparison.Ordinal) Then Return g
            Next
            Return Nothing
        End Function

        ''' <summary>DER Chokepoint für „wird dieses Objekt gezeichnet?": eigenes IsVisible UND die
        ''' Sichtbarkeit seiner Gruppe. Jede Renderstelle, die bisher `annotation.IsVisible` gelesen hat,
        ''' muss hierüber gehen - sonst blendet der Gruppenschalter im Panel nichts aus.</summary>
        Public Function IsAnnotationRenderVisible(annotation As ImageAnnotation) As Boolean
            If annotation Is Nothing OrElse Not annotation.IsVisible Then Return False
            Dim group = FindAnnotationGroup(annotation)
            Return group Is Nothing OrElse group.IsVisible
        End Function

        ''' <summary>Wie IsAnnotationRenderVisible, aber für lokale Korrekturebenen: eigene Sichtbarkeit
        ''' UND die ihrer Gruppe.</summary>
        ''' <summary>Ist dieses Objekt geometrisch gesperrt - eigenes Schloss oder das seiner Gruppe?
        ''' Der Renderer fragt das nie; es entscheidet allein, was der Editor zulässt.</summary>
        Public Function IsAnnotationGeometryLocked(annotation As ImageAnnotation) As Boolean
            If annotation Is Nothing Then Return False
            If annotation.IsLocked Then Return True
            If String.IsNullOrEmpty(annotation.GroupId) OrElse AnnotationGroups Is Nothing Then Return False
            For Each g In AnnotationGroups
                If g IsNot Nothing AndAlso String.Equals(g.Id, annotation.GroupId, StringComparison.Ordinal) Then Return g.IsLocked
            Next
            Return False
        End Function

        Public Function IsMaskedLayerRenderVisible(layer As MaskedAdjustmentLayer) As Boolean
            If layer Is Nothing OrElse Not layer.IsVisible Then Return False
            If String.IsNullOrEmpty(layer.GroupId) Then Return True
            If AnnotationGroups Is Nothing Then Return True
            For Each g In AnnotationGroups
                If g IsNot Nothing AndAlso String.Equals(g.Id, layer.GroupId, StringComparison.Ordinal) Then Return g.IsVisible
            Next
            Return True
        End Function

        ''' <summary>True, sobald irgendeine Pixel-Anpassung von der Voreinstellung abweicht. Nur dann muss
        ''' ein Objekt überhaupt über die (teure) eigene Ebene gerendert werden.</summary>
        Public Function HasPixelAdjustments() As Boolean
            Dim neutral = New ImageAdjustments()
            For Each p In PixelAdjustmentProperties()
                If Not Object.Equals(p.GetValue(Me), p.GetValue(neutral)) Then Return True
            Next
            Return False
        End Function


        Public Function Clone() As ImageAdjustments
            Dim result = New ImageAdjustments With {
                .Exposure = Exposure,
                .SourceWidthPixels = SourceWidthPixels,
                .SourceHeightPixels = SourceHeightPixels,
                .RecipeCoordinateVersion = RecipeCoordinateVersion,
                .WorkingImageVersion = WorkingImageVersion,
                .WorkingImageHasTransparency = WorkingImageHasTransparency,
                .BakedOperationsApplied = BakedOperationsApplied,
                .Brightness = Brightness,
                .Contrast = Contrast,
                .Saturation = Saturation,
                .Vibrance = Vibrance,
                .CalibrationRedHue = CalibrationRedHue,
                .CalibrationRedSaturation = CalibrationRedSaturation,
                .CalibrationGreenHue = CalibrationGreenHue,
                .CalibrationGreenSaturation = CalibrationGreenSaturation,
                .CalibrationBlueHue = CalibrationBlueHue,
                .CalibrationBlueSaturation = CalibrationBlueSaturation,
                .CalibrationShadowTint = CalibrationShadowTint,
                .Highlights = Highlights,
                .ShadowsLevel = ShadowsLevel,
                .Whites = Whites,
                .Blacks = Blacks,
                .Temperature = Temperature,
                .Tint = Tint,
                .Sharpness = Sharpness,
                .NoiseReduction = NoiseReduction,
                .NoiseReductionMethod = NoiseReductionMethod,
                .ColorNoiseReduction = ColorNoiseReduction,
                .FarbrauschGrob = FarbrauschGrob,
                .ColorNoiseCoarseScale = ColorNoiseCoarseScale,
                .ColorNoiseAdd = ColorNoiseAdd,
                .DustScratches = DustScratches,
                .Haze = Haze,
                .AddNoise = AddNoise,
                .Structure = [Structure],
                .Glow = Glow,
                .Vignette = Vignette,
                .VignetteTransition = VignetteTransition,
                .VignetteRoundness = VignetteRoundness,
                .VignetteFeather = VignetteFeather,
                .VignetteCenterX = VignetteCenterX,
                .VignetteCenterY = VignetteCenterY,
                .Grain = Grain,
                .Clarity = Clarity,
                .NegativeEnabled = NegativeEnabled,
                .NegativeMonochrome = NegativeMonochrome,
                .NegativeBaseColor = NegativeBaseColor,
                .NegativeDensityColor = NegativeDensityColor,
                .NegativeGamma = NegativeGamma,
                .CurveRgbPoints = CurveRgbPoints,
                .CurveRedPoints = CurveRedPoints,
                .CurveGreenPoints = CurveGreenPoints,
                .CurveBluePoints = CurveBluePoints,
                .CurveLuminancePoints = CurveLuminancePoints,
                .RedHue = RedHue,
                .RedSaturation = RedSaturation,
                .RedLuminance = RedLuminance,
                .OrangeHue = OrangeHue,
                .OrangeSaturation = OrangeSaturation,
                .OrangeLuminance = OrangeLuminance,
                .YellowHue = YellowHue,
                .YellowSaturation = YellowSaturation,
                .YellowLuminance = YellowLuminance,
                .GreenHue = GreenHue,
                .GreenSaturation = GreenSaturation,
                .GreenLuminance = GreenLuminance,
                .AquaHue = AquaHue,
                .AquaSaturation = AquaSaturation,
                .AquaLuminance = AquaLuminance,
                .BlueHue = BlueHue,
                .BlueSaturation = BlueSaturation,
                .BlueLuminance = BlueLuminance,
                .PurpleHue = PurpleHue,
                .PurpleSaturation = PurpleSaturation,
                .PurpleLuminance = PurpleLuminance,
                .MagentaHue = MagentaHue,
                .MagentaSaturation = MagentaSaturation,
                .MagentaLuminance = MagentaLuminance,
                .ColorGradeShadowHue = ColorGradeShadowHue,
                .ColorGradeShadowSaturation = ColorGradeShadowSaturation,
                .ColorGradeShadowLuminance = ColorGradeShadowLuminance,
                .ColorGradeMidtoneHue = ColorGradeMidtoneHue,
                .ColorGradeMidtoneSaturation = ColorGradeMidtoneSaturation,
                .ColorGradeMidtoneLuminance = ColorGradeMidtoneLuminance,
                .ColorGradeHighlightHue = ColorGradeHighlightHue,
                .ColorGradeHighlightSaturation = ColorGradeHighlightSaturation,
                .ColorGradeHighlightLuminance = ColorGradeHighlightLuminance,
                .ColorGradeGlobalHue = ColorGradeGlobalHue,
                .ColorGradeGlobalSaturation = ColorGradeGlobalSaturation,
                .ColorGradeGlobalLuminance = ColorGradeGlobalLuminance,
                .ColorGradeBalance = ColorGradeBalance,
                .ColorGradeBlending = ColorGradeBlending,
                .RotationDegrees = RotationDegrees,
                .PerspectiveHorizontal = PerspectiveHorizontal,
                .PerspectiveVertical = PerspectiveVertical,
                .PerspectiveAspect = PerspectiveAspect,
                .PerspectiveScale = PerspectiveScale,
                .PerspectiveCorner0X = PerspectiveCorner0X, .PerspectiveCorner0Y = PerspectiveCorner0Y,
                .PerspectiveCorner1X = PerspectiveCorner1X, .PerspectiveCorner1Y = PerspectiveCorner1Y,
                .PerspectiveCorner2X = PerspectiveCorner2X, .PerspectiveCorner2Y = PerspectiveCorner2Y,
                .PerspectiveCorner3X = PerspectiveCorner3X, .PerspectiveCorner3Y = PerspectiveCorner3Y,
                .ImageWarp = ImageWarp?.Clone(),
                .StraightenDegrees = StraightenDegrees,
                .StraightenExpandCanvas = StraightenExpandCanvas,
                .FlipHorizontal = FlipHorizontal,
                .FlipVertical = FlipVertical,
                .CropLeftPercent = CropLeftPercent,
                .CropTopPercent = CropTopPercent,
                .CropRightPercent = CropRightPercent,
                .CropBottomPercent = CropBottomPercent,
                .ResizeWidth = ResizeWidth,
                .ResizeHeight = ResizeHeight,
                .LockResizeAspect = LockResizeAspect,
                .ResizeFitInsideBox = ResizeFitInsideBox,
                .ResizeScalePercent = ResizeScalePercent,
                .NoResizeUpscale = NoResizeUpscale,
                .ResizeInterpolation = ResizeInterpolation,
                .UpscaleModel = UpscaleModel,
                .CanvasWidth = CanvasWidth,
                .CanvasHeight = CanvasHeight,
                .LockCanvasAspect = LockCanvasAspect,
                .CanvasAnchor = CanvasAnchor,
                .CanvasBackgroundColor = CanvasBackgroundColor,
                .FilterPreset = FilterPreset,
                .FilterStrength = FilterStrength,
                .LutPath = LutPath,
                .LutStrength = LutStrength,
                .RetouchSpots = RetouchSpots.Select(Function(s) s.Clone()).ToList(),
                .BakedOperations = BakedOperations.Select(Function(o) o.Clone()).ToList(),
                .Annotations = Annotations.Select(Function(a) a.Clone()).ToList(),
                .AnnotationGroups = If(AnnotationGroups, New System.Collections.Generic.List(Of AnnotationGroup)()).
                    Where(Function(g) g IsNot Nothing).Select(Function(g) g.Clone()).ToList(),
                .RasterPaintStrokes = RasterPaintStrokes.Select(Function(s) s.Clone()).ToList(),
                .Masks = If(Masks, New List(Of ImageMask)()).Where(Function(m) m IsNot Nothing).Select(Function(m) m.Clone()).ToList(),
                .MaskedAdjustmentLayers = If(MaskedAdjustmentLayers, New List(Of MaskedAdjustmentLayer)()).Where(Function(l) l IsNot Nothing).Select(Function(l) l.Clone()).ToList(),
                .SelectionScopeEnabled = SelectionScopeEnabled,
                .HasActiveSelection = HasActiveSelection,
                .ActiveSelectionIsMask = ActiveSelectionIsMask,
                .SelectionXPercent = SelectionXPercent,
                .SelectionYPercent = SelectionYPercent,
                .SelectionWidthPercent = SelectionWidthPercent,
                .SelectionHeightPercent = SelectionHeightPercent,
                .SelectionShapeMode = SelectionShapeMode,
                .SelectionShapePointsX = If(SelectionShapePointsX Is Nothing, Nothing, SelectionShapePointsX.ToArray()),
                .SelectionShapePointsY = If(SelectionShapePointsY Is Nothing, Nothing, SelectionShapePointsY.ToArray()),
                .SelectionMaskLeft = SelectionMaskLeft,
                .SelectionMaskTop = SelectionMaskTop,
                .SelectionMaskRight = SelectionMaskRight,
                .SelectionMaskBottom = SelectionMaskBottom,
                .SelectionMaskPngBase64 = SelectionMaskPngBase64,
                .SelectionFeatherPixels = SelectionFeatherPixels,
                .SelectionMaskSoftBaked = SelectionMaskSoftBaked,
                .GlobalAdjustmentsHidden = GlobalAdjustmentsHidden,
                .BackgroundHidden = BackgroundHidden,
                .PixelLayerHidden = PixelLayerHidden
            }
            ' Die explizite Liste oben hält die strukturellen/deep-copy-Felder lesbar. Pixelwerte werden
            ' zusätzlich zentral kopiert, damit ein neu ergänzter Regler nicht in Undo/FPX/Layern fehlt.
            result.CopyPixelAdjustmentsFrom(Me)
            Return result
        End Function
    End Class

End Namespace
