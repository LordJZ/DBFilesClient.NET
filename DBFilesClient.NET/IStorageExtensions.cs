
namespace DBFilesClient.NET
{
    public static class IStorageExtensions
    {
        public static unsafe void* GetEntryPtr(this IStorage storage, uint id)
        {
            return storage.GetEntry(id).ToPointer();
        }
    }
}
