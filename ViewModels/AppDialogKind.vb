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
        WatermarkPreset
    End Enum

    Public Enum FileConflictChoice
        Cancel
        Skip
        Overwrite
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
        Public Property TextQuery As String
        Public Property RootFolder As String
        Public Property IncludeSubfolders As Boolean
        Public Property FavoriteMode As String
        Public Property RatingMin As Integer
        Public Property Ratings As List(Of Integer)
        Public Property Conditions As List(Of SearchCondition)
        Public Property ConditionCombinator As String
    End Class

    Public Class BatchResizeResult
        Public Property Width As Integer
        Public Property Height As Integer
        Public Property ScalePercent As Integer
        Public Property LockAspect As Boolean
        Public Property Interpolation As ResizeInterpolationMode
    End Class

    Public Class WatermarkPresetDialogResult
        Public Property Preset As WatermarkPresetSettings
    End Class

End Namespace
