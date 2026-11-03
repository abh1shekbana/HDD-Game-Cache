/*++
Module Name:
    Communication.c

Abstract:
    Filter Manager communication port implementation for SmartGameCache.
    Provides secure, low-latency IPC between kernel driver and user-mode engine.
--*/

#include "MiniFilter.h"

NTSTATUS
SmartCachePortConnect(
    _In_ PFLT_PORT ClientPort,
    _In_opt_ PVOID ServerPortCookie,
    _In_reads_bytes_opt_(SizeOfContext) PVOID ConnectionContext,
    _In_ ULONG SizeOfContext,
    _Outptr_result_maybenull_ PVOID *ConnectionPortCookie
)
{
    UNREFERENCED_PARAMETER(ServerPortCookie);
    UNREFERENCED_PARAMETER(ConnectionContext);
    UNREFERENCED_PARAMETER(SizeOfContext);

    g_FilterData.ClientPort = ClientPort;
    *ConnectionPortCookie = NULL;
    return STATUS_SUCCESS;
}

VOID
SmartCachePortDisconnect(
    _In_opt_ PVOID ConnectionCookie
)
{
    UNREFERENCED_PARAMETER(ConnectionCookie);

    if (g_FilterData.ClientPort != NULL) {
        FltCloseClientPort(g_FilterData.FilterHandle, &g_FilterData.ClientPort);
        g_FilterData.ClientPort = NULL;
    }
}

NTSTATUS
SmartCachePortMessage(
    _In_opt_ PVOID PortCookie,
    _In_reads_bytes_opt_(InputBufferSize) PVOID InputBuffer,
    _In_ ULONG InputBufferSize,
    _Out_writes_bytes_to_opt_(OutputBufferSize, *ReturnOutputBufferLength) PVOID OutputBuffer,
    _In_ ULONG OutputBufferSize,
    _Out_ PULONG ReturnOutputBufferLength
)
{
    UNREFERENCED_PARAMETER(PortCookie);

    *ReturnOutputBufferLength = 0;

    if (InputBuffer == NULL || InputBufferSize < sizeof(SMART_CACHE_MESSAGE)) {
        return STATUS_INVALID_PARAMETER;
    }

    PSMART_CACHE_MESSAGE msg = (PSMART_CACHE_MESSAGE)InputBuffer;

    switch (msg->Command) {
        case CMD_ATTACH_GAME_PROCESS:
            g_FilterData.MonitoredGamePid = msg->ProcessId;
            break;

        case CMD_DETACH_GAME_PROCESS:
            g_FilterData.MonitoredGamePid = 0;
            break;

        case CMD_QUERY_DRIVER_STATS:
            if (OutputBuffer != NULL && OutputBufferSize >= sizeof(SMART_CACHE_REPLY)) {
                PSMART_CACHE_REPLY reply = (PSMART_CACHE_REPLY)OutputBuffer;
                reply->Status = STATUS_SUCCESS;
                reply->TotalInterceptedReads = g_FilterData.TotalInterceptedReads;
                reply->TotalBytesServedFromSSD = g_FilterData.TotalBytesServedFromSSD;
                *ReturnOutputBufferLength = sizeof(SMART_CACHE_REPLY);
            }
            break;

        default:
            return STATUS_NOT_SUPPORTED;
    }

    return STATUS_SUCCESS;
}

NTSTATUS
SmartCacheInitializeCommunication(
    _In_ PFLT_FILTER FilterHandle
)
{
    NTSTATUS status;
    OBJECT_ATTRIBUTES oa;
    UNICODE_STRING portName;
    PSECURITY_DESCRIPTOR sd = NULL;

    RtlInitUnicodeString(&portName, SMARTCACHE_PORT_NAME);

    // Build security descriptor permitting LocalSystem and Administrators
    status = FltBuildDefaultSecurityDescriptor(&sd, FLT_PORT_ALL_ACCESS);
    if (!NT_SUCCESS(status)) {
        return status;
    }

    InitializeObjectAttributes(&oa, &portName, OBJ_CASE_INSENSITIVE | OBJ_KERNEL_HANDLE, NULL, sd);

    status = FltCreateCommunicationPort(
        FilterHandle,
        &g_FilterData.ServerPort,
        &oa,
        NULL,
        SmartCachePortConnect,
        SmartCachePortDisconnect,
        SmartCachePortMessage,
        1
    );

    FltFreeSecurityDescriptor(sd);
    return status;
}

VOID
SmartCacheCloseCommunication(
    VOID
)
{
    if (g_FilterData.ServerPort != NULL) {
        FltCloseCommunicationPort(g_FilterData.ServerPort);
        g_FilterData.ServerPort = NULL;
    }
}

