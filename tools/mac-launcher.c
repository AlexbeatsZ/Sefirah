#include <mach-o/dyld.h>
#include <dlfcn.h>
#include <limits.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>
#include <stdint.h>

typedef int32_t (*HostfxrMain)(int, const char **, const char *, const char *, const char *);

/* Host .NET inside the bundle's main executable. execv into Resources loses
   NSBundle.mainBundle identity and prevents native Notification Center access. */
int main(int argc, char **argv) {
    char executable[PATH_MAX], canonical[PATH_MAX], host[PATH_MAX];
    char runtime_dir[PATH_MAX], assembly[PATH_MAX], library[PATH_MAX];
    uint32_t size = sizeof executable;
    if (_NSGetExecutablePath(executable, &size) != 0 || realpath(executable, canonical) == NULL) {
        perror("Resolve Sefirah launcher");
        return 1;
    }
    memcpy(host, canonical, strlen(canonical) + 1);
    char *separator = strrchr(canonical, '/');
    if (separator == NULL) return 1;
    *separator = '\0';
    separator = strrchr(canonical, '/');
    if (separator == NULL) return 1;
    *separator = '\0';
    int length = snprintf(runtime_dir, sizeof runtime_dir, "%s/Resources/runtime", canonical);
    if (length < 0 || (size_t)length >= sizeof runtime_dir) return 1;
    length = snprintf(assembly, sizeof assembly, "%s/Sefirah.Desktop.dll", runtime_dir);
    if (length < 0 || (size_t)length >= sizeof assembly) return 1;
    length = snprintf(library, sizeof library, "%s/libhostfxr.dylib", runtime_dir);
    if (length < 0 || (size_t)length >= sizeof library) return 1;
    if (chdir(runtime_dir) != 0) {
        perror("Set Sefirah runtime directory");
        return 1;
    }
    void *runtime = dlopen(library, RTLD_NOW | RTLD_LOCAL);
    if (runtime == NULL) {
        fprintf(stderr, "Load Sefirah runtime: %s\n", dlerror());
        return 1;
    }
    HostfxrMain start = (HostfxrMain)dlsym(runtime, "hostfxr_main_startupinfo");
    if (start == NULL) {
        fprintf(stderr, "Resolve Sefirah runtime entry: %s\n", dlerror());
        return 1;
    }
    argv[0] = host;
    return start(argc, (const char **)argv, host, runtime_dir, assembly);
}
