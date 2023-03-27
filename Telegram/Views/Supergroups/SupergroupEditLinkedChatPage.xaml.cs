//
// Copyright Fela Ameghino 2015-2023
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//
using Telegram.Common;
using Telegram.Controls;
using Telegram.Td.Api;
using Telegram.ViewModels.Delegates;
using Telegram.ViewModels.Supergroups;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Telegram.Views.Supergroups
{
    public sealed partial class SupergroupEditLinkedChatPage : HostedPage, ISupergroupDelegate
    {
        public SupergroupEditLinkedChatViewModel ViewModel => DataContext as SupergroupEditLinkedChatViewModel;

        public SupergroupEditLinkedChatPage()
        {
            InitializeComponent();
            Title = Strings.Discussion;
        }

        private void OnElementPrepared(Microsoft.UI.Xaml.Controls.ItemsRepeater sender, Microsoft.UI.Xaml.Controls.ItemsRepeaterElementPreparedEventArgs args)
        {
            var button = args.Element as Button;
            var content = button.Content as Grid;

            var chat = button.DataContext as Chat;

            var title = content.Children[1] as TextBlock;
            title.Text = ViewModel.ClientService.GetTitle(chat);

            if (ViewModel.ClientService.TryGetSupergroup(chat, out Supergroup supergroup))
            {
                var subtitle = content.Children[2] as TextBlock;
                if (supergroup.HasActiveUsername(out string username))
                {
                    subtitle.Text = $"@{username}";
                }
                else
                {
                    subtitle.Text = Locale.Declension(supergroup.IsChannel ? Strings.R.Subscribers : Strings.R.Members, supergroup.MemberCount);
                }
            }

            var photo = content.Children[0] as ProfilePicture;
            photo.SetChat(ViewModel.ClientService, chat, 36);

            button.Command = ViewModel.LinkCommand;
            button.CommandParameter = chat;
        }

        #region Delegate

        public void UpdateSupergroup(Chat chat, Supergroup group)
        {
            TextBlockHelper.SetMarkdown(Headline, string.Format(Strings.DiscussionChannelGroupSetHelp2, chat.Title));

            Create.Visibility = group.HasLinkedChat ? Visibility.Collapsed : Visibility.Visible;
            Unlink.Visibility = group.HasLinkedChat ? Visibility.Visible : Visibility.Collapsed;
            Unlink.Content = group.IsChannel ? Strings.DiscussionUnlinkGroup : Strings.DiscussionUnlinkChannel;
        }

        public void UpdateSupergroupFullInfo(Chat chat, Supergroup group, SupergroupFullInfo fullInfo)
        {
            var linkedChat = ViewModel.ClientService.GetChat(fullInfo.LinkedChatId);
            if (linkedChat != null)
            {
                if (group.IsChannel)
                {
                    TextBlockHelper.SetMarkdown(Headline, string.Format(Strings.DiscussionChannelGroupSetHelp2, linkedChat.Title));
                    LayoutRoot.Footer = Strings.DiscussionChannelHelp2;
                }
                else
                {
                    TextBlockHelper.SetMarkdown(Headline, string.Format(Strings.DiscussionGroupHelp, linkedChat.Title));
                    LayoutRoot.Footer = Strings.DiscussionChannelHelp2;
                }

                JoinToSendMessages.Visibility = group.IsChannel ? Visibility.Visible : Visibility.Collapsed;
            }
            else
            {
                JoinToSendMessages.Visibility = Visibility.Collapsed;
            }
        }

        public void UpdateChat(Chat chat) { }
        public void UpdateChatTitle(Chat chat) { }
        public void UpdateChatPhoto(Chat chat) { }

        #endregion

        #region Binding

        private string ConvertJoinToSendMessages(bool joinToSendMessages)
        {
            return joinToSendMessages ? Strings.ChannelSettingsJoinRequestInfo : Strings.ChannelSettingsJoinToSendInfo;
        }

        #endregion
    }
}