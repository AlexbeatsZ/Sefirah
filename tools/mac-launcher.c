#include <mach-o/dyld.h>
#include <limits.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>

/* Keep managed assemblies in Resources, outside macOS's nested-code locations. */
int main(int argc, char **argv) {
    (void)argc;
    char executable[PATH_MAX], canonical[PATH_MAX], runtime_dir[PATH_MAX], runtime[PATH_MAX];
    uint32_t size = sizeof executable;
    if (_NSGetExecutablePath(executable, &size) != 0 || realpath(executable, canonical) == NULL) {
        perror("Resolve Sefirah launcher");
        return 1;
    }
    char *separator = strrchr(canonical, '/');
    if (separator == NULL) return 1;
    *separator = '\0';
    separator = strrchr(canonical, '/');
    if (separator == NULL) return 1;
    *separator = '\0';
    int length = snprintf(runtime_dir, sizeof runtime_dir, "%s/Resources/runtime", canonical);
    if (length < 0 || (size_t)length >= sizeof runtime_dir) return 1;
    length = snprintf(runtime, sizeof runtime, "%s/Sefirah.Desktop", runtime_dir);
    if (length < 0 || (size_t)length >= sizeof runtime) return 1;
    if (chdir(runtime_dir) != 0) {
        perror("Set Sefirah runtime directory");
        return 1;
    }
    argv[0] = runtime;
    execv(runtime, argv);
    perror("Start Sefirah runtime");
    return 1;
}
