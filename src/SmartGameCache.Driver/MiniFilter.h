/*++
Module Name:
    MiniFilter.h

Abstract:
    Header definitions for SmartGameCache Windows Filesystem Minifilter.
    Implements transparent read redirection, communication port, and stream contexts.
--*/

#pragma once

#include <fltKernel.h>
#include <dontuse.h>
#include <suppress.h>

#define SMARTCACHE_PORT_NAME L"\\SmartGameCachePort"
#define SMARTCACHE_ALTITUDE  L"370050"

// Commands exchanged with user-mode service
typedef enum _SMART_CACHE_COMMAND {
    CMD_ATTACH_GAME_PROCESS   = 1,
    CMD_DETACH_GAME_PROCESS   = 2,
    CMD_REGISTER_CACHED_CHUNK = 3,
    CMD_INVALIDATE_CHUNKS     = 4,
    CMD_QUERY_DRIVER_STATS    = 5
} SMART_CACHE_COMMAND;

typedef struct _SMART_CACHE_MESSAGE {
    SMART_CACHE_COMMAND Command;
    ULONG ProcessId;
    ULONG ChunkIndex;
    INT64 ChunkOffset;
    ULONG ChunkLength;
    WCHAR RelativePath[260];
} SMART_CACHE_MESSAGE, *PSMART_CACHE_MESSAGE;

typedef struct _SMART_CACHE_REPLY {
    NTSTATUS Status;
    INT64 TotalInterceptedReads;
    INT64 TotalBytesServedFromSSD;
} SMART_CACHE_REPLY, *PSMART_CACHE_REPLY;

// Stream Context attached to open files of monitored games
typedef struct _SMART_STREAM_CONTEXT {
    BOOLEAN IsMonitored;
    ULONG TargetProcessId;
    WCHAR RelativePath[260];
} SMART_STREAM_CONTEXT, *PSMART_STREAM_CONTEXT;

#define SMART_STREAM_CONTEXT_SIZE sizeof(SMART_STREAM_CONTEXT)
#define SMART_STREAM_CONTEXT_TAG  'cSmS'

// Globals
typedef struct _SMART_FILTER_DATA {
    PFLT_FILTER FilterHandle;
    PFLT_PORT ServerPort;
    PFLT_PORT ClientPort;
    ULONG MonitoredGamePid;
    INT64 TotalInterceptedReads;
    INT64 TotalBytesServedFromSSD;
} SMART_FILTER_DATA, *PSMART_FILTER_DATA;

extern SMART_FILTER_DATA g_FilterData;

// Callbacks
FLT_PREOP_CALLBACK_STATUS
SmartCachePreCreate(
    _Inout_ PFLT_CALLBACK_DATA Data,
    _In_ PCFLT_RELATED_OBJECTS FltObjects,
    _Flt_CompletionContext_Outptr_ PVOID *CompletionContext
);

FLT_POSTOP_CALLBACK_STATUS
SmartCachePostCreate(
    _Inout_ PFLT_CALLBACK_DATA Data,
    _In_ PCFLT_RELATED_OBJECTS FltObjects,
    _In_opt_ PVOID CompletionContext,
    _In_ FLT_POST_OPERATION_FLAGS Flags
);

FLT_PREOP_CALLBACK_STATUS
SmartCachePreRead(
    _Inout_ PFLT_CALLBACK_DATA Data,
    _In_ PCFLT_RELATED_OBJECTS FltObjects,
    _Flt_CompletionContext_Outptr_ PVOID *CompletionContext
);

FLT_POSTOP_CALLBACK_STATUS
SmartCachePostRead(
    _Inout_ PFLT_CALLBACK_DATA Data,
    _In_ PCFLT_RELATED_OBJECTS FltObjects,
    _In_opt_ PVOID CompletionContext,
    _In_ FLT_POST_OPERATION_FLAGS Flags
);

FLT_PREOP_CALLBACK_STATUS
SmartCachePreFileSystemControl(
    _Inout_ PFLT_CALLBACK_DATA Data,
    _In_ PCFLT_RELATED_OBJECTS FltObjects,
    _Flt_CompletionContext_Outptr_ PVOID *CompletionContext
);

// Communication Port
NTSTATUS
SmartCacheInitializeCommunication(
    _In_ PFLT_FILTER FilterHandle
);

VOID
SmartCacheCloseCommunication(
    VOID
);

