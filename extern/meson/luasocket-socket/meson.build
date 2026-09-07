# socket/core — luasocket's socket module (variables from the root meson.build)
shared_module(
  'core',
  luasocket_src,
  name_prefix: '',
  c_args: luasocket_args,
  include_directories: lua_inc,
  link_with: lua54,
  dependencies: luasocket_deps,
  install: true,
  install_dir: 'Lua/socket',
)
