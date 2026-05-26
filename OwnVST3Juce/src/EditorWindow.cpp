#include "EditorWindow.h"

EditorWindow::EditorWindow(juce::AudioProcessorEditor* editor,
                           const juce::String& title,
                           std::function<void()> onClose)
    : juce::DocumentWindow(
          title,
          juce::Desktop::getInstance()
              .getDefaultLookAndFeel()
              .findColour(juce::ResizableWindow::backgroundColourId),
          juce::DocumentWindow::allButtons)
    , _onClose(std::move(onClose))
{
    setUsingNativeTitleBar(true);

    // DocumentWindow takes ownership of the editor component.
    setContentOwned(editor, true);

    setResizable(editor->isResizable(), false);
    centreWithSize(editor->getWidth(), editor->getHeight());

    setVisible(true);
    toFront(true);

    _open = true;
}

EditorWindow::~EditorWindow()
{
    _open = false;
}

void EditorWindow::show()
{
    setVisible(true);
    toFront(true);
    _open = true;
}

void EditorWindow::hide()
{
    setVisible(false);
    _open = false;
}

bool EditorWindow::isWindowOpen() const noexcept
{
    return _open && isVisible();
}

void EditorWindow::resizeTo(int width, int height)
{
    setSize(width, height);

    if (auto* content = getContentComponent())
        content->setSize(width, height);
}

bool EditorWindow::getContentSize(int& width, int& height) const
{
    if (const auto* content = getContentComponent())
    {
        width  = content->getWidth();
        height = content->getHeight();
        return width > 0 && height > 0;
    }
    return false;
}

void EditorWindow::runIdle()
{
    // JUCE-based editors use internal Timer mechanisms for idle processing.
    // Native/legacy editors that expose IPlugView::onIdle() are called by
    // JUCE's AudioPluginInstance automatically when the editor is attached.
    // No explicit action is required here for standard JUCE editors.
}

void EditorWindow::closeButtonPressed()
{
    _open = false;
    setVisible(false);

    if (_onClose)
        _onClose();
}
