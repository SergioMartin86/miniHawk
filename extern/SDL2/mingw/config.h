/* config.h — manual libusb config for mingw-w64 (miniHawk Linux-hosted cross
 * builds). Mirrors libusb/msvc/config.h minus the MSVC-only guards/pragmas. */

/* Define to the attribute for default visibility. */
#define DEFAULT_VISIBILITY /**/

/* Define to 1 to enable message logging. */
#define ENABLE_LOGGING 1

/* Define to 1 if compiling for a Windows platform. */
#define PLATFORM_WINDOWS 1

/* Define to the attribute for enabling parameter checks on printf-like
   functions. */
#define PRINTF_FORMAT(a, b) __attribute__ ((__format__ (__printf__, a, b)))

/* NOTE: do NOT define _TIMESPEC_DEFINED here — that macro is mingw-w64's own
   guard for struct timespec in <time.h>; defining it would suppress the
   definition libusb needs. */
