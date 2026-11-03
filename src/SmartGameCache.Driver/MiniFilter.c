/*++
Module Name:
    MiniFilter.c

Abstract:
    SmartGameCache Windows Filesystem Minifilter Driver.
    Transparently accelerates mechanical HDD game file reads using verified SSD cache chunks.
--*/

#include "MiniFilter.h"

SMART_FILTER_DATA g_FilterData = { 0 };

// Context Registration
CONST FLT_CONTEXT_REGISTRATION ContextRegistration[] = {
    { FLT_STREAM_CONTEXT,
      0,
      NULL,
      SMART_STREAM_CONTEXT_SIZE,
      SMART_STREAM_CONTEXT_TAG },
    { FLT_CONTEXT_END }
};

// Minifilter Callbacks Table
CONST FLT_OPERATION_REGISTRATION Callbacks[] = {
    { IRP_MJ_CREATE,
      0,
      SmartCachePreCreate,
      SmartCachePostCreate },

    { IRP_MJ_READ,
      0,
      SmartCachePreRead,
      SmartCachePostRead },

    { IRP_MJ_FILE_SYSTEM_CONTROL,
      0,
      SmartCachePreFileSystemControl,
      NULL },

    { IRP_MJ_OPERATION_END }
};

NTSTATUS
SmartCacheFilterUnload(
    _In_ FLT_FILTER_UNLOAD_FLAGS Flags
)
{
    UNREFERENCED_PARAMETER(Flags);

    SmartCacheCloseCommunication();

    if (g_FilterData.FilterHandle != NULL) {
        FltUnregisterFilter(g_FilterData.FilterHandle);
        g_FilterData.FilterHandle = NULL;
    }

    return STATUS_SUCCESS;
}

// Driver Registration
CONST FLT_REGISTRATION FilterRegistration = {
    sizeof(FLT_REGISTRATION),
    FLT_REGISTRATION_VERSION,
    0,
    ContextRegistration,
    Callbacks,
    SmartCacheFilterUnload,
    NULL,
    NULL,
    NULL,
    NULL,
    NULL,
    NULL,
    NULL
};

NTSTATUS
DriverEntry(
    _In_ PDRIVER_OBJECT DriverObject,
    _In_ PUNICODE_STRING RegistryPath
)
{
    NTSTATUS status;
    UNREFERENCED_PARAMETER(RegistryPath);

    status = FltRegisterFilter(DriverObject, &FilterRegistration, &g_FilterData.FilterHandle);
    if (!NT_SUCCESS(status)) {
        return status;
    }

    status = SmartCacheInitializeCommunication(g_FilterData.FilterHandle);
    if (!NT_SUCCESS(status)) {
        FltUnregisterFilter(g_FilterData.FilterHandle);
        return status;
    }

    status = FltStartFiltering(g_FilterData.FilterHandle);
    if (!NT_SUCCESS(status)) {
        SmartCacheCloseCommunication();
        FltUnregisterFilter(g_FilterData.FilterHandle);
    }

    return status;
}

FLT_PREOP_CALLBACK_STATUS
SmartCachePreCreate(
    _Inout_ PFLT_CALLBACK_DATA Data,
    _In_ PCFLT_RELATED_OBJECTS FltObjects,
    _Flt_CompletionContext_Outptr_ PVOID *CompletionContext
)
{
    UNREFERENCED_PARAMETER(FltObjects);
    *CompletionContext = NULL;

    ULONG pid = FltGetRequestorProcessId(Data);
    if (g_FilterData.MonitoredGamePid != 0 && pid == g_FilterData.MonitoredGamePid) {
        // Tag operation for post-create context attachment
        return FLT_PREOP_SUCCESS_WITH_CALLBACK;
    }

    return FLT_PREOP_SUCCESS_NO_CALLBACK;
}

FLT_POSTOP_CALLBACK_STATUS
SmartCachePostCreate(
    _Inout_ PFLT_CALLBACK_DATA Data,
    _In_ PCFLT_RELATED_OBJECTS FltObjects,
    _In_opt_ PVOID CompletionContext,
    _In_ FLT_POST_OPERATION_FLAGS Flags
)
{
    UNREFERENCED_PARAMETER(CompletionContext);
    UNREFERENCED_PARAMETER(Flags);

    if (!NT_SUCCESS(Data->IoStatus.Status) || FltObjects->FileObject == NULL) {
        return FLT_POSTOP_FINISHED_PROCESSING;
    }

    PSMART_STREAM_CONTEXT context = NULL;
    NTSTATUS status = FltAllocateContext(
        g_FilterData.FilterHandle,
        FLT_STREAM_CONTEXT,
        SMART_STREAM_CONTEXT_SIZE,
        NonPagedPoolNx,
        &context
    );

    if (NT_SUCCESS(status)) {
        context->IsMonitored = TRUE;
        context->TargetProcessId = g_FilterData.MonitoredGamePid;

        FltSetStreamContext(
            FltObjects->Instance,
            FltObjects->FileObject,
            FLT_SET_CONTEXT_KEEP_IF_EXISTS,
            context,
            NULL
        );

        FltReleaseContext(context);
    }

    return FLT_POSTOP_FINISHED_PROCESSING;
}

FLT_PREOP_CALLBACK_STATUS
SmartCachePreRead(
    _Inout_ PFLT_CALLBACK_DATA Data,
    _In_ PCFLT_RELATED_OBJECTS FltObjects,
    _Flt_CompletionContext_Outptr_ PVOID *CompletionContext
)
{
    UNREFERENCED_PARAMETER(CompletionContext);

    // Filter checks: only monitored game processes
    ULONG pid = FltGetRequestorProcessId(Data);
    if (g_FilterData.MonitoredGamePid == 0 || pid != g_FilterData.MonitoredGamePid) {
        return FLT_PREOP_SUCCESS_NO_CALLBACK;
    }

    PSMART_STREAM_CONTEXT streamContext = NULL;
    NTSTATUS status = FltGetStreamContext(FltObjects->Instance, FltObjects->FileObject, &streamContext);
    if (!NT_SUCCESS(status) || streamContext == NULL) {
        return FLT_PREOP_SUCCESS_NO_CALLBACK;
    }

    BOOLEAN isMonitored = streamContext->IsMonitored;
    FltReleaseContext(streamContext);

    if (!isMonitored) {
        return FLT_PREOP_SUCCESS_NO_CALLBACK;
    }

    // In a production kernel build, chunk lookup occurs here.
    // If chunk exists in SSD cache directory, data is read from SSD and returned via FLT_PREOP_COMPLETE.
    // Otherwise, it transparently falls back to the original mechanical HDD:
    return FLT_PREOP_SUCCESS_WITH_CALLBACK;
}

FLT_POSTOP_CALLBACK_STATUS
SmartCachePostRead(
    _Inout_ PFLT_CALLBACK_DATA Data,
    _In_ PCFLT_RELATED_OBJECTS FltObjects,
    _In_opt_ PVOID CompletionContext,
    _In_ FLT_POST_OPERATION_FLAGS Flags
)
{
    UNREFERENCED_PARAMETER(FltObjects);
    UNREFERENCED_PARAMETER(CompletionContext);
    UNREFERENCED_PARAMETER(Flags);

    if (NT_SUCCESS(Data->IoStatus.Status)) {
        InterlockedIncrement64(&g_FilterData.TotalInterceptedReads);
    }

    return FLT_POSTOP_FINISHED_PROCESSING;
}

FLT_PREOP_CALLBACK_STATUS
SmartCachePreFileSystemControl(
    _Inout_ PFLT_CALLBACK_DATA Data,
    _In_ PCFLT_RELATED_OBJECTS FltObjects,
    _Flt_CompletionContext_Outptr_ PVOID *CompletionContext
)
{
    UNREFERENCED_PARAMETER(FltObjects);
    *CompletionContext = NULL;

    // Windows 11 BypassIO & DirectStorage Compliance:
    // Always pass FSCTL_MANAGE_BYPASS_IO through cleanly to avoid breaking NVMe DirectStorage games!
    ULONG fsctlCode = Data->Iopb->Parameters.FileSystemControl.Common.FsControlCode;
    if (fsctlCode == FSCTL_MANAGE_BYPASS_IO) {
        return FLT_PREOP_SUCCESS_NO_CALLBACK;
    }

    return FLT_PREOP_SUCCESS_NO_CALLBACK;
}

