# mime/core — luasocket's mime module (variables from the root meson.build)
shared_module(
  'core',
  files('../../luasocket/src/mime.c', '../../luasocket/src/compat.c'),
  name_prefix: '',
  c_args: host_windows ? ['-DMIME_API=__declspec(dllexport)'] : [],
  include_directories: lua_inc,
  link_with: lua54,
  install: true,
  install_dir: 'Lua/mime',
)
