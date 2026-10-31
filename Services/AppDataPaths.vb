Imports System.IO

Namespace Services
    ' An optional profile root lets development builds run without sharing installed data.
    Public NotInheritable Class AppDataPaths
        Public Shared Function GetFolderPath(folder As Environment.SpecialFolder) As String
            Dim root = Environment.GetEnvironmentVariable("FERRUMPIX_PROFILE_ROOT")
            If String.IsNullOrWhiteSpace(root) Then
                Dim config = Path.Combine(AppContext.BaseDirectory, "profile-root.txt")
                If File.Exists(config) Then root = Environment.ExpandEnvironmentVariables(File.ReadAllText(config).Trim())
            End If
            If String.IsNullOrWhiteSpace(root) Then Return Environment.GetFolderPath(folder)
            If Not Path.IsPathFullyQualified(root) Then Throw New ArgumentException("FERRUMPIX_PROFILE_ROOT must be absolute.")
            Return Path.Combine(root, If(folder = Environment.SpecialFolder.ApplicationData, "Roaming", "Local"))
        End Function
    End Class
End Namespace
