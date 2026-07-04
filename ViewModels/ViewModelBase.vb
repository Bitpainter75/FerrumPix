Imports ReactiveUI

Namespace ViewModels

    Public MustInherit Class ViewModelBase
        Inherits ReactiveObject
    End Class

    Public Enum AppMode
        Gallery = 0
        Viewer = 1
        Editor = 2
        Settings = 3
    End Enum

End Namespace
