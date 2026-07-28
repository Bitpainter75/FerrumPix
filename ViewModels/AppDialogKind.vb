Imports System.Collections.Generic
Imports FerrumPix.Services

Namespace ViewModels

    Public Enum AppDialogKind
        Message
        Input
        Rename
        SaveAs
        FileConflict
        BatchRename
        Search
        BatchConvert
        BatchResize
        BatchFilter
        WatermarkPreset
        ExportTo
    End Enum

    ''' <summary>Auswahl aus dem Dialog "Exportieren nach" (Galerie): ein Sammel-Export, der
    ''' Namensmuster, Look/Auto-Verbesserung, Wasserzeichen, Bildgröße, Metadaten und Format in
    ''' einem Durchgang anbietet. Originale werden NIE überschrieben - es entstehen immer neue
    ''' Dateien (die Zielpfad-Bildung weicht vorhandenen Dateien aus).</summary>
    Public Class ExportToDialogResult
        ''' <summary>Automatische Bildverbesserung: pro Bild messen und die Grundregler setzen.</summary>
        Public Property AutoEnhance As Boolean
        ''' <summary>"" = kein Look; sonst BatchFilterDialogResult.SourceFilter/SourceXmpPreset/SourceLut.</summary>
        Public Property LookKind As String = ""
        Public Property LookName As String = ""
        Public Property LookPath As String = ""
        Public Property LookStrength As Integer = 100
        ''' <summary>Name der Wasserzeichen-Vorgabe; leer = kein Wasserzeichen.</summary>
        Public Property WatermarkPresetName As String = ""
        ''' <summary>Die Vorgabe MIT den Dialogwerten (Anker, Breite) - eine Kopie, die nur fuer
        ''' diesen Lauf gilt. Nothing, wenn kein Wasserzeichen gewaehlt ist.</summary>
        Public Property WatermarkPreset As WatermarkPresetSettings
        ''' <summary>True: die Maße des Wasserzeichens gelten fuer das FERTIGE Bild - es wird also
        ''' erst nach dem Verkleinern in seiner eingestellten Groesse aufgebracht und ist in jeder
        ''' Ausgabegroesse gleich gross. False (Vorgabe): die Maße gelten fuer das Originalbild und
        ''' schrumpfen mit.</summary>
        Public Property WatermarkKeepSize As Boolean
        ''' <summary>0 = Originalmaß. Wie die Bildgrößen-Stapelfunktion über ImageAdjustments.Resize*;
        ''' ist nur EINE Kante gesetzt, gilt sie als längste Kante (siehe ImageProcessor.ApplyResize).</summary>
        Public Property ResizeWidth As Integer
        Public Property ResizeHeight As Integer
        ''' <summary>&gt;0 = prozentuale Skalierung statt fester Maße (wie im Bildgrößen-Dialog).</summary>
        Public Property ResizeScalePercent As Integer
        Public Property LockAspect As Boolean = True
        ''' <summary>Bilder, die schon kleiner als das Ziel sind, bleiben unveraendert.</summary>
        Public Property NoUpscale As Boolean
        Public Property ResizeInterpolation As ResizeInterpolationMode = ResizeInterpolationMode.Bilinear
        ''' <summary>EXIF/XMP der Quelle in die Zieldatei übernehmen.</summary>
        Public Property PreserveMetadata As Boolean = True
        ''' <summary>Dateinamen-Muster für die Ziele (leer = Originalname), Platzhalter wie beim
        ''' Stapel-Umbenennen.</summary>
        Public Property NamePattern As String = ""
        Public Property Format As String = "JPG"
        Public Property JpgQuality As Integer = 90
        Public Property Target As String = "Local"
        Public Property TargetFolder As String = ""
        Public Property CopyRating As Boolean = True
        Public Property CopyFavorite As Boolean = True
        Public Property CopyColorLabel As Boolean = True
        Public Property CopyKeywords As Boolean = True

        Public ReadOnly Property MetaCopy As CatalogMetaCopyOptions
            Get
                Return New CatalogMetaCopyOptions With {
                    .CopyRating = CopyRating, .CopyFavorite = CopyFavorite,
                    .CopyColorLabel = CopyColorLabel, .CopyKeywords = CopyKeywords}
            End Get
        End Property

        Public ReadOnly Property Extension As String
            Get
                Select Case If(Format, "").Trim().ToUpperInvariant()
                    Case "PNG" : Return ".png"
                    Case "WEBP" : Return ".webp"
                    Case "PDF" : Return ".pdf"
                    Case "FPX" : Return ".fpx"
                    Case Else : Return ".jpg"
                End Select
            End Get
        End Property

        ''' <summary>Ein Projektbündel ist eine lokale Datei - Immich führt Bild-Assets, keine
        ''' Dokumente (dieselbe Regel wie beim Speichern unter).</summary>
        Public ReadOnly Property IsFpx As Boolean
            Get
                Return String.Equals(If(Format, "").Trim(), "FPX", StringComparison.OrdinalIgnoreCase)
            End Get
        End Property
    End Class

    Public Enum FileConflictChoice
        Cancel
        Skip
        SkipAll
        Overwrite
        OverwriteAll
        Rename
    End Enum

    Public Class FileConflictDialogResult
        Public Property Choice As FileConflictChoice
        Public Property NewName As String
    End Class

    Public Class BatchRenameMapping
        Public Property SourcePath As String
        Public Property TargetPath As String
    End Class

    Public Class BatchRenameResult
        Public Property Mappings As List(Of BatchRenameMapping)
    End Class

    Public Class SearchDialogResult
        Public Property Name As String
        ''' "Local" (Dateisystem) oder "Immich" (Server-Suche).
        Public Property Source As String = "Local"
        Public Property TextQuery As String
        Public Property RootFolder As String
        Public Property IncludeSubfolders As Boolean
        Public Property FavoriteMode As String
        Public Property RatingMin As Integer
        Public Property Ratings As List(Of Integer)
        Public Property Conditions As List(Of SearchCondition)
        Public Property ConditionCombinator As String
    End Class

    ''' <summary>Welche Katalog-Metadaten eine neu geschriebene Datei vom Original erbt - die Zeile
    ''' „Übernehmen" gibt es in mehreren Dialogen, deshalb tragen die Ergebnisse sie einheitlich.</summary>
    Public Class CatalogMetaCopyOptions
        Public Property CopyRating As Boolean = True
        Public Property CopyFavorite As Boolean = True
        Public Property CopyColorLabel As Boolean = True
        Public Property CopyKeywords As Boolean = True
    End Class

    Public Class BatchResizeResult
        ''' <summary>Datei-Metadaten (EXIF/XMP) der Quelle in die Zieldatei uebernehmen -
        ''' der Knopf "EXIF" im Uebernehmen-Bereich des Dialogs.</summary>
        Public Property PreserveMetadata As Boolean = True
        Public Property Width As Integer
        Public Property Height As Integer
        Public Property ScalePercent As Integer
        Public Property LockAspect As Boolean
        ''' <summary>Bilder, die schon kleiner als das Ziel sind, bleiben unveraendert.</summary>
        Public Property NoUpscale As Boolean
        Public Property Interpolation As ResizeInterpolationMode

        ''' <summary>True: Originale werden überschrieben (bisheriges Verhalten; Format, Ziel und
        ''' Namenszusatz entfallen). False: neue Dateien mit Formatauswahl wie beim Filter-Dialog.</summary>
        Public Property Overwrite As Boolean = True

        Public Property Format As String = "JPG"
        Public Property JpgQuality As Integer = 90

        ''' <summary>Zielort für neue Dateien: "Local" (Ordner) oder "Immich" (Upload als neues Asset).</summary>
        Public Property Target As String = "Local"
        Public Property TargetFolder As String = ""

        ''' <summary>Welche Katalog-Metadaten die Kopie vom Original übernimmt (Zeile „Übernehmen" im
        ''' Dialog). Beim Überschreiben ohne Bedeutung - die Datei behält ihren Katalog-Eintrag.</summary>
        Public Property CopyRating As Boolean = True
        Public Property CopyFavorite As Boolean = True
        Public Property CopyColorLabel As Boolean = True
        Public Property CopyKeywords As Boolean = True

        ''' <summary>Dateinamen-Muster für die Zieldateien (leer = Originalname behalten).</summary>
        Public Property NamePattern As String = ""

        Public ReadOnly Property MetaCopy As CatalogMetaCopyOptions
            Get
                Return New CatalogMetaCopyOptions With {
                    .CopyRating = CopyRating,
                    .CopyFavorite = CopyFavorite,
                    .CopyColorLabel = CopyColorLabel,
                    .CopyKeywords = CopyKeywords
                }
            End Get
        End Property

        Public ReadOnly Property Extension As String
            Get
                Select Case If(Format, "").Trim().ToUpperInvariant()
                    Case "PNG"
                        Return ".png"
                    Case "WEBP"
                        Return ".webp"
                    Case Else
                        Return ".jpg"
                End Select
            End Get
        End Property

    End Class

    Public Class WatermarkPresetDialogResult
        ''' <summary>Datei-Metadaten (EXIF/XMP) der Quelle in die Zieldatei uebernehmen -
        ''' der Knopf "EXIF" im Uebernehmen-Bereich des Dialogs.</summary>
        Public Property PreserveMetadata As Boolean = True
        Public Property Preset As WatermarkPresetSettings
        Public Property Overwrite As Boolean = True
        ''' Muster fuer die Zieldateinamen. Der Dialog zeigt das Feld schon laenger; ohne diese
        ''' Eigenschaft lief die Eingabe ins Leere.
        Public Property NamePattern As String = ""
        Public Property Format As String = "JPG"
        Public Property JpgQuality As Integer = 90
        Public Property Target As String = "Local"
        Public Property TargetFolder As String = ""
        Public Property CopyRating As Boolean = True
        Public Property CopyFavorite As Boolean = True
        Public Property CopyColorLabel As Boolean = True
        Public Property CopyKeywords As Boolean = True

        Public ReadOnly Property MetaCopy As CatalogMetaCopyOptions
            Get
                Return New CatalogMetaCopyOptions With {
                    .CopyRating = CopyRating,
                    .CopyFavorite = CopyFavorite,
                    .CopyColorLabel = CopyColorLabel,
                    .CopyKeywords = CopyKeywords
                }
            End Get
        End Property

        Public ReadOnly Property Extension As String
            Get
                Select Case If(Format, "").Trim().ToUpperInvariant()
                    Case "PNG"
                        Return ".png"
                    Case "WEBP"
                        Return ".webp"
                    Case Else
                        Return ".jpg"
                End Select
            End Get
        End Property
    End Class

End Namespace
