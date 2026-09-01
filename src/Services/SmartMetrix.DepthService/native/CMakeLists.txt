cmake_minimum_required(VERSION 3.20)
project(smartmetrix_stereo LANGUAGES CXX)
option(SMARTMETRIX_WITH_OPENCV_CUDA "Build the OpenCV CUDA StereoSGM provider" ON)

add_library(smartmetrix_stereo SHARED smartmetrix_stereo.cpp)
target_compile_features(smartmetrix_stereo PRIVATE cxx_std_17)
target_include_directories(smartmetrix_stereo PUBLIC ${CMAKE_CURRENT_SOURCE_DIR})
set_target_properties(smartmetrix_stereo PROPERTIES VERSION 1.0.0 SOVERSION 1)

if(SMARTMETRIX_WITH_OPENCV_CUDA)
  find_package(OpenCV REQUIRED COMPONENTS core calib3d cudastereo)
  target_compile_definitions(smartmetrix_stereo PRIVATE SMARTMETRIX_WITH_OPENCV_CUDA=1)
  target_include_directories(smartmetrix_stereo PRIVATE ${OpenCV_INCLUDE_DIRS})
  target_link_libraries(smartmetrix_stereo PRIVATE ${OpenCV_LIBS})
endif()

include(GNUInstallDirs)
install(TARGETS smartmetrix_stereo LIBRARY DESTINATION ${CMAKE_INSTALL_LIBDIR} RUNTIME DESTINATION ${CMAKE_INSTALL_BINDIR})
install(FILES smartmetrix_stereo.h DESTINATION ${CMAKE_INSTALL_INCLUDEDIR})
